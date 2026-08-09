using System.Data;
using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>PostgreSQL adapter for lease-safe delivery of exact Ingestion commands to Catalog.</summary>
public sealed partial class PostgresIngestionCatalogDeliveryStore : IIngestionCatalogDeliveryStore
{
    private readonly IngestionProcessingDbContext _dbContext;
    private readonly IIngestionProcessingStore _processingStore;

    public PostgresIngestionCatalogDeliveryStore(
        IngestionProcessingDbContext dbContext,
        IIngestionProcessingStore processingStore)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _processingStore = processingStore ?? throw new ArgumentNullException(nameof(processingStore));
    }

    public async Task<IReadOnlyList<IngestionCatalogDeliveryLease>> LeaseAsync(
        string workerIdentity,
        int limit,
        DateTimeOffset leasedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateLeaseRequest(workerIdentity, limit, leasedAtUtc, leaseExpiresAtUtc);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var connection = RequireConnection();
        var dbTransaction = RequireTransaction(transaction);
        var candidates = new List<DeliveryCandidate>(limit);
        await using (var command = new NpgsqlCommand(
            """
            SELECT d.delivery_id, d.batch_id, d.item_key, d.command_type, d.command_document,
                   d.command_digest, d.attempt_count
            FROM processing.catalog_delivery d
            INNER JOIN batches.import_batch b ON b.id = d.batch_id
            WHERE b.state = @committing
              AND (
                  (d.state = 1 AND (d.next_attempt_at_utc IS NULL OR d.next_attempt_at_utc <= @now))
                  OR (d.state = 2 AND d.lease_expires_at_utc <= @now)
              )
            ORDER BY d.created_at_utc, d.delivery_id
            FOR UPDATE OF d SKIP LOCKED
            LIMIT @limit;
            """,
            connection,
            dbTransaction))
        {
            command.Parameters.Add(new NpgsqlParameter<int>(
                "committing",
                (int)ImportBatchState.Committing));
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", leasedAtUtc));
            command.Parameters.Add(new NpgsqlParameter<int>("limit", limit));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new DeliveryCandidate(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetFieldValue<byte[]>(4),
                    reader.GetString(5),
                    reader.GetInt32(6)));
            }
        }

        var leases = new List<IngestionCatalogDeliveryLease>(candidates.Count);
        foreach (var candidate in candidates)
        {
            CatalogIngestionUpsertDraftCommand catalogCommand;
            try
            {
                catalogCommand = VerifyCommand(candidate);
            }
            catch (IngestionApplicationException exception)
            {
                await RejectCorruptDeliveryAsync(
                    connection,
                    dbTransaction,
                    candidate,
                    exception,
                    leasedAtUtc,
                    cancellationToken);
                continue;
            }

            var leaseToken = Guid.CreateVersion7();
            await using var update = new NpgsqlCommand(
                """
                UPDATE processing.catalog_delivery
                SET state = 2,
                    attempt_count = attempt_count + 1,
                    worker_identity = @worker,
                    lease_token = @lease_token,
                    lease_expires_at_utc = @lease_expires,
                    next_attempt_at_utc = NULL,
                    last_changed_at_utc = @now
                WHERE delivery_id = @delivery_id;
                """,
                connection,
                dbTransaction);
            update.Parameters.Add(new NpgsqlParameter<string>("worker", workerIdentity.Trim()));
            update.Parameters.Add(new NpgsqlParameter<Guid>("lease_token", leaseToken));
            update.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("lease_expires", leaseExpiresAtUtc));
            update.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", leasedAtUtc));
            update.Parameters.Add(new NpgsqlParameter<Guid>("delivery_id", candidate.DeliveryId));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Failure(
                    "INGESTION_DELIVERY_LEASE_WRITE_FAILED",
                    500,
                    $"Catalog delivery '{candidate.DeliveryId:D}' could not be leased.",
                    "Retry after inspecting the Ingestion database transaction state.");
            }

            leases.Add(new IngestionCatalogDeliveryLease(
                candidate.DeliveryId,
                candidate.BatchId,
                candidate.ItemKey,
                leaseToken,
                leaseExpiresAtUtc,
                catalogCommand,
                candidate.CommandDigest,
                candidate.AttemptCount + 1));
        }

        await transaction.CommitAsync(cancellationToken);
        return leases;
    }

    public async Task<IngestionProcessingSnapshot> RecordOutcomeAsync(
        IngestionCatalogDeliveryResult result,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        RequireUtc(completedAtUtc, nameof(completedAtUtc));
        ValidateOutcome(result.Outcome);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var connection = RequireConnection();
        var dbTransaction = RequireTransaction(transaction);
        var current = await ReadForUpdateAsync(connection, dbTransaction, result.DeliveryId, cancellationToken);
        EnsureIdentity(current, result.BatchId, result.ItemKey, result.Outcome);
        var targetState = result.Outcome.State == CatalogIngestionOutcomeStateContract.Rejected ? 4 : 3;
        if (current.State is 3 or 4)
        {
            EnsureTerminalOutcomeMatches(current, targetState, result.Outcome);
        }
        else
        {
            EnsureActiveLease(current, result.LeaseToken, completedAtUtc);
            await using var update = new NpgsqlCommand(
                """
                UPDATE processing.catalog_delivery
                SET state = @state,
                    worker_identity = NULL,
                    lease_token = NULL,
                    lease_expires_at_utc = NULL,
                    next_attempt_at_utc = NULL,
                    catalog_listing_id = @listing_id,
                    catalog_listing_revision_id = @listing_revision_id,
                    failure_code = @failure_code,
                    failure_detail = @failure_detail,
                    last_changed_at_utc = @now
                WHERE delivery_id = @delivery_id
                  AND state = 2
                  AND lease_token = @lease_token
                  AND lease_expires_at_utc > @now;
                """,
                connection,
                dbTransaction);
            update.Parameters.Add(new NpgsqlParameter<int>("state", targetState));
            AddNullable(update, "listing_id", result.Outcome.ListingId);
            AddNullable(update, "listing_revision_id", result.Outcome.ListingRevisionId);
            AddNullable(update, "failure_code", result.Outcome.FailureCode);
            AddNullable(update, "failure_detail", result.Outcome.FailureDetail);
            update.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", completedAtUtc));
            update.Parameters.Add(new NpgsqlParameter<Guid>("delivery_id", result.DeliveryId));
            update.Parameters.Add(new NpgsqlParameter<Guid>("lease_token", result.LeaseToken));
            EnsureLeaseMutation(await update.ExecuteNonQueryAsync(cancellationToken), result.DeliveryId);
        }

        await FinalizeBatchIfTerminalAsync(connection, dbTransaction, current.BatchId, completedAtUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await RequireSnapshotAsync(current.BatchId, cancellationToken);
    }

    public async Task ScheduleRetryAsync(
        IngestionCatalogDeliveryFailure failure,
        DateTimeOffset nextAttemptAtUtc,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateFailure(failure);
        RequireUtc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
        RequireUtc(failedAtUtc, nameof(failedAtUtc));
        if (nextAttemptAtUtc <= failedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(nextAttemptAtUtc));
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var connection = RequireConnection();
        var dbTransaction = RequireTransaction(transaction);
        var current = await ReadForUpdateAsync(connection, dbTransaction, failure.DeliveryId, cancellationToken);
        EnsureIdentity(current, failure.BatchId, failure.ItemKey, null);
        EnsureActiveLease(current, failure.LeaseToken, failedAtUtc);
        await using var update = new NpgsqlCommand(
            """
            UPDATE processing.catalog_delivery
            SET state = 1,
                worker_identity = NULL,
                lease_token = NULL,
                lease_expires_at_utc = NULL,
                next_attempt_at_utc = @next_attempt,
                failure_code = @failure_code,
                failure_detail = @failure_detail,
                last_changed_at_utc = @now
            WHERE delivery_id = @delivery_id
              AND state = 2
              AND lease_token = @lease_token
              AND lease_expires_at_utc > @now;
            """,
            connection,
            dbTransaction);
        update.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("next_attempt", nextAttemptAtUtc));
        update.Parameters.Add(new NpgsqlParameter<string>("failure_code", failure.FailureCode));
        update.Parameters.Add(new NpgsqlParameter<string>("failure_detail", failure.FailureDetail));
        update.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", failedAtUtc));
        update.Parameters.Add(new NpgsqlParameter<Guid>("delivery_id", failure.DeliveryId));
        update.Parameters.Add(new NpgsqlParameter<Guid>("lease_token", failure.LeaseToken));
        EnsureLeaseMutation(await update.ExecuteNonQueryAsync(cancellationToken), failure.DeliveryId);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IngestionProcessingSnapshot> FailAsync(
        IngestionCatalogDeliveryFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateFailure(failure);
        RequireUtc(failedAtUtc, nameof(failedAtUtc));
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var connection = RequireConnection();
        var dbTransaction = RequireTransaction(transaction);
        var current = await ReadForUpdateAsync(connection, dbTransaction, failure.DeliveryId, cancellationToken);
        EnsureIdentity(current, failure.BatchId, failure.ItemKey, null);
        if (current.State == 4)
        {
            if (!string.Equals(current.FailureCode, failure.FailureCode, StringComparison.Ordinal) ||
                !string.Equals(current.FailureDetail, failure.FailureDetail, StringComparison.Ordinal))
            {
                throw Failure(
                    "INGESTION_DELIVERY_FAILURE_CONFLICT",
                    409,
                    "The Catalog delivery already has a different terminal failure.",
                    "Use the exact original terminal delivery failure.");
            }
        }
        else if (current.State == 3)
        {
            throw Failure(
                "INGESTION_DELIVERY_ALREADY_SUCCEEDED",
                409,
                "A successful Catalog delivery cannot be replaced with a failure.",
                "Use the retained successful Catalog outcome.");
        }
        else
        {
            EnsureActiveLease(current, failure.LeaseToken, failedAtUtc);
            await using var update = new NpgsqlCommand(
                """
                UPDATE processing.catalog_delivery
                SET state = 4,
                    worker_identity = NULL,
                    lease_token = NULL,
                    lease_expires_at_utc = NULL,
                    next_attempt_at_utc = NULL,
                    catalog_listing_id = NULL,
                    catalog_listing_revision_id = NULL,
                    failure_code = @failure_code,
                    failure_detail = @failure_detail,
                    last_changed_at_utc = @now
                WHERE delivery_id = @delivery_id
                  AND state = 2
                  AND lease_token = @lease_token
                  AND lease_expires_at_utc > @now;
                """,
                connection,
                dbTransaction);
            update.Parameters.Add(new NpgsqlParameter<string>("failure_code", failure.FailureCode));
            update.Parameters.Add(new NpgsqlParameter<string>("failure_detail", failure.FailureDetail));
            update.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", failedAtUtc));
            update.Parameters.Add(new NpgsqlParameter<Guid>("delivery_id", failure.DeliveryId));
            update.Parameters.Add(new NpgsqlParameter<Guid>("lease_token", failure.LeaseToken));
            EnsureLeaseMutation(await update.ExecuteNonQueryAsync(cancellationToken), failure.DeliveryId);
        }

        await FinalizeBatchIfTerminalAsync(connection, dbTransaction, current.BatchId, failedAtUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await RequireSnapshotAsync(current.BatchId, cancellationToken);
    }
}
