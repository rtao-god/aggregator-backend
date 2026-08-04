using System.Data;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>
/// Owns durable package claims and atomically persists all items, issues, initial decisions,
/// batch state and terminal work result. A failed transaction leaves no partial package rows.
/// </summary>
public sealed class EfIngestionPackageWorkRepository(
    IngestionDbContext dbContext,
    IIngestionIdSource idSource) : IIngestionPackageWorkRepository
{
    private readonly IngestionDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IIngestionIdSource _idSource = idSource ?? throw new ArgumentNullException(nameof(idSource));

    public Task<IngestionPackageWorkClaim?> ClaimNextAsync(
        string workerIdentity,
        DateTimeOffset leasedAtUtc,
        TimeSpan leaseLifetime,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerIdentity);
        if (workerIdentity.Length > 200 || leasedAtUtc.Offset != TimeSpan.Zero ||
            leaseLifetime < TimeSpan.FromSeconds(30) || leaseLifetime > TimeSpan.FromMinutes(30))
        {
            throw new IngestionApplicationException(
                "Ingestion.Worker",
                "INGESTION_WORK_CLAIM_INVALID",
                500,
                "The package work claim parameters are invalid.",
                "Correct the worker identity, UTC clock or configured lease lifetime.");
        }

        return UseConnectionAsync(
            connection => ClaimNextCoreAsync(
                connection,
                workerIdentity,
                leasedAtUtc,
                leaseLifetime,
                cancellationToken),
            cancellationToken);
    }

    public Task CompleteAsync(
        IngestionPackageWorkClaim claim,
        ImportBatch batch,
        IngestionPackageValidationResult validation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(validation);
        if (batch.Id != claim.Batch.Id ||
            batch.AggregateRevision != claim.Batch.AggregateRevision + 3 ||
            batch.State is not ImportBatchState.ReadyToCommit and not ImportBatchState.ReviewRequired)
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_PACKAGE_COMPLETION_STATE_INVALID",
                500,
                "Package completion must advance integrity, item-validation and decision transitions exactly once.",
                "Correct the package-processing application workflow before persistence.");
        }

        if (validation.Items.Count != batch.ExpectedItemCount ||
            validation.AcceptedItemCount != batch.AcceptedItemCount ||
            validation.ReviewRequiredItemCount != batch.ReviewRequiredItemCount ||
            validation.RejectedItemCount != batch.RejectedItemCount)
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_PACKAGE_COMPLETION_COUNTS_INVALID",
                500,
                "The package validation result and final batch counts diverge.",
                "Correct the exact item-decision aggregation before persistence.");
        }

        return UseConnectionAsync(
            connection => CompleteCoreAsync(
                connection,
                claim,
                batch,
                validation,
                cancellationToken),
            cancellationToken);
    }

    public Task FailIntegrityAsync(
        IngestionPackageWorkClaim claim,
        ImportBatch batch,
        string failureCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (batch.Id != claim.Batch.Id ||
            batch.AggregateRevision != claim.Batch.AggregateRevision + 1 ||
            batch.State != ImportBatchState.IntegrityFailed ||
            !string.Equals(batch.FailureCode, failureCode, StringComparison.Ordinal))
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_PACKAGE_FAILURE_STATE_INVALID",
                500,
                "Package integrity failure does not match the claimed aggregate transition.",
                "Correct the package failure workflow before persistence.");
        }

        return UseConnectionAsync(
            connection => FailCoreAsync(
                connection,
                claim,
                batch,
                failureCode,
                cancellationToken),
            cancellationToken);
    }

    private async Task<IngestionPackageWorkClaim?> ClaimNextCoreAsync(
        NpgsqlConnection connection,
        string workerIdentity,
        DateTimeOffset leasedAtUtc,
        TimeSpan leaseLifetime,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using (var seed = new NpgsqlCommand(
            """
            INSERT INTO operations.package_validation_work
                (batch_id, status, attempt_count, claim_id, worker_identity, leased_until_utc,
                 last_failure_code, created_at_utc, updated_at_utc)
            SELECT b.id, 1, 0, NULL, NULL, NULL, NULL, @now, @now
            FROM batches.import_batch AS b
            WHERE b.state = 3
            ON CONFLICT (batch_id) DO NOTHING;
            """,
            connection,
            transaction))
        {
            Add(seed, "now", NpgsqlDbType.TimestampTz, leasedAtUtc);
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        Guid? batchId = null;
        var storedState = 0;
        await using (var select = new NpgsqlCommand(
            """
            SELECT w.batch_id, b.state
            FROM operations.package_validation_work AS w
            INNER JOIN batches.import_batch AS b ON b.id = w.batch_id
            WHERE
                (w.status = 1 AND b.state = 3)
                OR
                (w.status = 2 AND b.state = 4 AND w.leased_until_utc <= @now)
            ORDER BY b.registered_at_utc, b.id
            FOR UPDATE OF w, b SKIP LOCKED
            LIMIT 1;
            """,
            connection,
            transaction))
        {
            Add(select, "now", NpgsqlDbType.TimestampTz, leasedAtUtc);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                batchId = reader.GetGuid(0);
                storedState = reader.GetInt32(1);
            }
        }

        if (batchId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (storedState == (int)ImportBatchState.Uploaded)
        {
            await using var transition = new NpgsqlCommand(
                """
                UPDATE batches.import_batch
                SET state = 4,
                    aggregate_revision = aggregate_revision + 1,
                    last_changed_at_utc = GREATEST(last_changed_at_utc, @now)
                WHERE id = @batch_id AND state = 3;
                """,
                connection,
                transaction);
            Add(transition, "now", NpgsqlDbType.TimestampTz, leasedAtUtc);
            Add(transition, "batch_id", NpgsqlDbType.Uuid, batchId.Value);
            if (await transition.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw ClaimConflict(batchId.Value);
            }
        }

        var claimId = _idSource.CreateId();
        if (claimId == Guid.Empty)
        {
            throw new IngestionApplicationException(
                "Ingestion.Worker",
                "INGESTION_WORK_CLAIM_ID_INVALID",
                500,
                "The package worker generated an empty claim identity.",
                "Correct the UUIDv7 identity source.");
        }

        var leasedUntilUtc = leasedAtUtc + leaseLifetime;
        int attemptNumber;
        await using (var updateWork = new NpgsqlCommand(
            """
            UPDATE operations.package_validation_work
            SET status = 2,
                attempt_count = attempt_count + 1,
                claim_id = @claim_id,
                worker_identity = @worker_identity,
                leased_until_utc = @leased_until_utc,
                last_failure_code = NULL,
                updated_at_utc = @now
            WHERE batch_id = @batch_id
            RETURNING attempt_count;
            """,
            connection,
            transaction))
        {
            Add(updateWork, "claim_id", NpgsqlDbType.Uuid, claimId);
            Add(updateWork, "worker_identity", NpgsqlDbType.Text, workerIdentity);
            Add(updateWork, "leased_until_utc", NpgsqlDbType.TimestampTz, leasedUntilUtc);
            Add(updateWork, "now", NpgsqlDbType.TimestampTz, leasedAtUtc);
            Add(updateWork, "batch_id", NpgsqlDbType.Uuid, batchId.Value);
            var value = await updateWork.ExecuteScalarAsync(cancellationToken);
            attemptNumber = value is int number
                ? number
                : throw ClaimConflict(batchId.Value);
        }

        var snapshot = await ReadSnapshotAsync(
            connection,
            transaction,
            batchId.Value,
            cancellationToken)
            ?? throw BatchNotFound(batchId.Value);
        if (snapshot.State != ImportBatchState.IntegrityChecking)
        {
            throw ClaimConflict(batchId.Value);
        }

        await transaction.CommitAsync(cancellationToken);
        return new IngestionPackageWorkClaim(
            claimId,
            attemptNumber,
            workerIdentity,
            leasedUntilUtc,
            snapshot);
    }

    private async Task CompleteCoreAsync(
        NpgsqlConnection connection,
        IngestionPackageWorkClaim claim,
        ImportBatch batch,
        IngestionPackageValidationResult validation,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await EnsureExactClaimAsync(connection, transaction, claim, cancellationToken);
        foreach (var item in validation.Items.OrderBy(value => value.Ordinal))
        {
            await InsertItemAsync(
                connection,
                transaction,
                batch.Id.Value,
                item,
                batch.LastChangedAtUtc,
                cancellationToken);
            foreach (var issue in item.QualityIssues)
            {
                await InsertIssueAsync(
                    connection,
                    transaction,
                    batch.Id.Value,
                    item.ItemKey,
                    issue,
                    batch.LastChangedAtUtc,
                    cancellationToken);
            }

            await InsertInitialDecisionAsync(
                connection,
                transaction,
                batch.Id.Value,
                item,
                claim.WorkerIdentity,
                batch.LastChangedAtUtc,
                cancellationToken);
        }

        await UpdateBatchAsync(
            connection,
            transaction,
            batch,
            claim.Batch.AggregateRevision,
            cancellationToken);
        await CompleteWorkAsync(
            connection,
            transaction,
            claim,
            status: 3,
            failureCode: null,
            batch.LastChangedAtUtc,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task FailCoreAsync(
        NpgsqlConnection connection,
        IngestionPackageWorkClaim claim,
        ImportBatch batch,
        string failureCode,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await EnsureExactClaimAsync(connection, transaction, claim, cancellationToken);
        await UpdateBatchAsync(
            connection,
            transaction,
            batch,
            claim.Batch.AggregateRevision,
            cancellationToken);
        await CompleteWorkAsync(
            connection,
            transaction,
            claim,
            status: 4,
            failureCode,
            batch.LastChangedAtUtc,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureExactClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionPackageWorkClaim claim,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT w.status, w.claim_id, w.worker_identity, w.leased_until_utc,
                   b.state, b.aggregate_revision
            FROM operations.package_validation_work AS w
            INNER JOIN batches.import_batch AS b ON b.id = w.batch_id
            WHERE w.batch_id = @batch_id
            FOR UPDATE OF w, b;
            """,
            connection,
            transaction);
        Add(command, "batch_id", NpgsqlDbType.Uuid, claim.Batch.Id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt32(0) != 2 ||
            reader.IsDBNull(1) || reader.GetGuid(1) != claim.ClaimId ||
            reader.IsDBNull(2) || !string.Equals(reader.GetString(2), claim.WorkerIdentity, StringComparison.Ordinal) ||
            reader.GetInt32(4) != (int)ImportBatchState.IntegrityChecking ||
            reader.GetInt64(5) != claim.Batch.AggregateRevision)
        {
            throw ClaimConflict(claim.Batch.Id.Value);
        }
    }

    private static async Task InsertItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid batchId,
        IngestionValidatedPackageItem item,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO batches.ingestion_item
                (batch_id, item_key, ordinal, entity_kind, content_digest,
                 canonical_document, created_at_utc)
            VALUES
                (@batch_id, @item_key, @ordinal, @entity_kind, @content_digest,
                 @canonical_document, @created_at_utc);
            """,
            connection,
            transaction);
        Add(command, "batch_id", NpgsqlDbType.Uuid, batchId);
        Add(command, "item_key", NpgsqlDbType.Text, item.ItemKey);
        Add(command, "ordinal", NpgsqlDbType.Integer, item.Ordinal);
        Add(command, "entity_kind", NpgsqlDbType.Integer, (int)item.EntityKind);
        Add(command, "content_digest", NpgsqlDbType.Text, item.ContentDigest);
        Add(command, "canonical_document", NpgsqlDbType.Bytea, item.CanonicalDocument);
        Add(command, "created_at_utc", NpgsqlDbType.TimestampTz, createdAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertIssueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid batchId,
        string itemKey,
        IngestionPackageQualityIssueContract issue,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO batches.item_issue
                (issue_id, batch_id, item_key, code, severity, detail, created_at_utc)
            VALUES
                (@issue_id, @batch_id, @item_key, @code, @severity, @detail, @created_at_utc);
            """,
            connection,
            transaction);
        Add(command, "issue_id", NpgsqlDbType.Uuid, RequireId(_idSource.CreateId(), "quality issue"));
        Add(command, "batch_id", NpgsqlDbType.Uuid, batchId);
        Add(command, "item_key", NpgsqlDbType.Text, itemKey);
        Add(command, "code", NpgsqlDbType.Text, issue.Code);
        Add(command, "severity", NpgsqlDbType.Integer, (int)issue.Severity);
        Add(command, "detail", NpgsqlDbType.Text, issue.Detail);
        Add(command, "created_at_utc", NpgsqlDbType.TimestampTz, createdAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertInitialDecisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid batchId,
        IngestionValidatedPackageItem item,
        string workerIdentity,
        DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken)
    {
        await using (var current = new NpgsqlCommand(
            """
            INSERT INTO batches.item_decision_current
                (batch_id, item_key, decision, reason_codes, decision_revision,
                 actor_identity, decided_at_utc)
            VALUES
                (@batch_id, @item_key, @decision, @reason_codes, 1,
                 @actor_identity, @decided_at_utc);
            """,
            connection,
            transaction))
        {
            Add(current, "batch_id", NpgsqlDbType.Uuid, batchId);
            Add(current, "item_key", NpgsqlDbType.Text, item.ItemKey);
            Add(current, "decision", NpgsqlDbType.Integer, (int)item.Decision);
            Add(current, "reason_codes", NpgsqlDbType.Array | NpgsqlDbType.Text, item.ReasonCodes.ToArray());
            Add(current, "actor_identity", NpgsqlDbType.Text, workerIdentity);
            Add(current, "decided_at_utc", NpgsqlDbType.TimestampTz, decidedAtUtc);
            await current.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var history = new NpgsqlCommand(
            """
            INSERT INTO batches.item_decision_history
                (decision_id, batch_id, item_key, previous_decision_revision, decision_revision,
                 decision, reason_codes, actor_identity, decided_at_utc)
            VALUES
                (@decision_id, @batch_id, @item_key, NULL, 1,
                 @decision, @reason_codes, @actor_identity, @decided_at_utc);
            """,
            connection,
            transaction);
        Add(history, "decision_id", NpgsqlDbType.Uuid, RequireId(_idSource.CreateId(), "item decision"));
        Add(history, "batch_id", NpgsqlDbType.Uuid, batchId);
        Add(history, "item_key", NpgsqlDbType.Text, item.ItemKey);
        Add(history, "decision", NpgsqlDbType.Integer, (int)item.Decision);
        Add(history, "reason_codes", NpgsqlDbType.Array | NpgsqlDbType.Text, item.ReasonCodes.ToArray());
        Add(history, "actor_identity", NpgsqlDbType.Text, workerIdentity);
        Add(history, "decided_at_utc", NpgsqlDbType.TimestampTz, decidedAtUtc);
        await history.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImportBatch batch,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE batches.import_batch
            SET last_changed_at_utc = @last_changed_at_utc,
                state = @state,
                aggregate_revision = @aggregate_revision,
                accepted_item_count = @accepted_item_count,
                review_required_item_count = @review_required_item_count,
                rejected_item_count = @rejected_item_count,
                failure_code = @failure_code
            WHERE id = @batch_id
              AND state = 4
              AND aggregate_revision = @expected_revision;
            """,
            connection,
            transaction);
        Add(command, "last_changed_at_utc", NpgsqlDbType.TimestampTz, batch.LastChangedAtUtc);
        Add(command, "state", NpgsqlDbType.Integer, (int)batch.State);
        Add(command, "aggregate_revision", NpgsqlDbType.Bigint, batch.AggregateRevision);
        Add(command, "accepted_item_count", NpgsqlDbType.Integer, batch.AcceptedItemCount);
        Add(command, "review_required_item_count", NpgsqlDbType.Integer, batch.ReviewRequiredItemCount);
        Add(command, "rejected_item_count", NpgsqlDbType.Integer, batch.RejectedItemCount);
        AddNullable(command, "failure_code", NpgsqlDbType.Text, batch.FailureCode);
        Add(command, "batch_id", NpgsqlDbType.Uuid, batch.Id.Value);
        Add(command, "expected_revision", NpgsqlDbType.Bigint, expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw ClaimConflict(batch.Id.Value);
        }
    }

    private static async Task CompleteWorkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionPackageWorkClaim claim,
        int status,
        string? failureCode,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE operations.package_validation_work
            SET status = @status,
                claim_id = NULL,
                worker_identity = NULL,
                leased_until_utc = NULL,
                last_failure_code = @failure_code,
                updated_at_utc = @completed_at_utc
            WHERE batch_id = @batch_id
              AND status = 2
              AND claim_id = @claim_id;
            """,
            connection,
            transaction);
        Add(command, "status", NpgsqlDbType.Integer, status);
        AddNullable(command, "failure_code", NpgsqlDbType.Text, failureCode);
        Add(command, "completed_at_utc", NpgsqlDbType.TimestampTz, completedAtUtc);
        Add(command, "batch_id", NpgsqlDbType.Uuid, claim.Batch.Id.Value);
        Add(command, "claim_id", NpgsqlDbType.Uuid, claim.ClaimId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw ClaimConflict(claim.Batch.Id.Value);
        }
    }

    private static async Task<IngestionBatchSnapshot?> ReadSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, producer_identity, producer_build, collector_export_id,
                   collector_export_digest, target_site_key, target_catalog_key,
                   target_catalog_configuration_revision_id, expected_item_count,
                   manifest_digest, item_index_digest, payload_digest, payload_object_key,
                   payload_object_digest, payload_object_size, payload_content_type,
                   registered_at_utc, last_changed_at_utc, state, aggregate_revision,
                   accepted_item_count, review_required_item_count, rejected_item_count,
                   failure_code
            FROM batches.import_batch
            WHERE id = @batch_id;
            """,
            connection,
            transaction);
        Add(command, "batch_id", NpgsqlDbType.Uuid, batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var stateValue = reader.GetInt32(18);
        if (!Enum.IsDefined(typeof(ImportBatchState), stateValue))
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_BATCH_STATE_CORRUPT",
                500,
                $"Import batch '{batchId:D}' contains unsupported state '{stateValue}'.",
                "Restore the batch from a verified Ingestion database backup.");
        }

        return new IngestionBatchSnapshot(
            ImportBatchId.Create(reader.GetGuid(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetGuid(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetGuid(7),
            reader.GetInt32(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetInt64(14),
            reader.GetString(15),
            reader.GetFieldValue<DateTimeOffset>(16),
            reader.GetFieldValue<DateTimeOffset>(17),
            (ImportBatchState)stateValue,
            reader.GetInt64(19),
            reader.GetInt32(20),
            reader.GetInt32(21),
            reader.GetInt32(22),
            reader.IsDBNull(23) ? null : reader.GetString(23));
    }

    private async Task<T> UseConnectionAsync<T>(
        Func<NpgsqlConnection, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)_dbContext.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            return await action(connection);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task UseConnectionAsync(
        Func<NpgsqlConnection, Task> action,
        CancellationToken cancellationToken)
    {
        await UseConnectionAsync(
            async connection =>
            {
                await action(connection);
                return true;
            },
            cancellationToken);
    }

    private static Guid RequireId(Guid value, string owner)
    {
        if (value == Guid.Empty)
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_PERSISTENCE_ID_INVALID",
                500,
                $"The {owner} identity source returned an empty ID.",
                "Correct the UUIDv7 identity source before processing packages.");
        }

        return value;
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    private static IngestionApplicationException ClaimConflict(Guid batchId) =>
        new(
            "Ingestion.Worker",
            "INGESTION_PACKAGE_CLAIM_CONFLICT",
            409,
            $"Package claim for import batch '{batchId:D}' is stale or no longer owned by this worker.",
            "Discard local work and acquire a new durable package claim.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["batchId"] = batchId,
            });

    private static IngestionApplicationException BatchNotFound(Guid batchId) =>
        new(
            "Ingestion.Batches",
            "INGESTION_BATCH_NOT_FOUND",
            404,
            $"Import batch '{batchId:D}' was not found.",
            "Use the exact registered import batch identity.");
}
