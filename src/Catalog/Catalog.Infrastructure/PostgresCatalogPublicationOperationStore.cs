using System.Data;
using Aggregator.Catalog.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Catalog.Infrastructure;

/// <summary>PostgreSQL owner adapter for durable Catalog publication operations.</summary>
public sealed class PostgresCatalogPublicationOperationStore(CatalogDbContext dbContext)
    : ICatalogPublicationOperationStore
{
    public async Task<CatalogPublicationOperationSnapshot> RegisterAsync(
        CatalogPublicationOperationRegistration registration,
        CancellationToken cancellationToken)
    {
        ValidateRegistration(registration);
        var publicationSequence = await AllocatePublicationSequenceAsync(
            registration.CatalogKey,
            cancellationToken);
        _ = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO catalog.publication_operation
            (
                id,
                publication_id,
                publication_sequence,
                catalog_key,
                actor_id,
                idempotency_key,
                request_document,
                request_digest,
                correlation_id,
                causation_id,
                state,
                attempt,
                created_at_utc,
                updated_at_utc
            )
            VALUES
            (
                {registration.OperationId},
                {registration.PublicationId},
                {publicationSequence},
                {registration.CatalogKey},
                {registration.ActorId},
                {registration.IdempotencyKey},
                {registration.RequestDocument},
                {registration.RequestDigest},
                {registration.CorrelationId},
                {registration.CausationId},
                {(int)CatalogPublicationOperationState.Pending},
                0,
                {registration.CreatedAtUtc},
                {registration.CreatedAtUtc}
            )
            ON CONFLICT (catalog_key, actor_id, idempotency_key) DO NOTHING;
            """, cancellationToken);

        var row = await dbContext.PublicationOperations
            .AsNoTracking()
            .SingleAsync(
                candidate => candidate.CatalogKey == registration.CatalogKey &&
                    candidate.ActorId == registration.ActorId &&
                    candidate.IdempotencyKey == registration.IdempotencyKey,
                cancellationToken);
        if (!string.Equals(row.RequestDigest, registration.RequestDigest, StringComparison.Ordinal))
        {
            throw new CatalogConflictException(
                $"Idempotency key '{registration.IdempotencyKey}' is already bound to a different Catalog publication request.");
        }

        return ToSnapshot(row);
    }

    public async Task<CatalogPublicationOperationSnapshot?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.PublicationOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == operationId, cancellationToken);
        return row is null ? null : ToSnapshot(row);
    }

    public async Task<CatalogPublicationOperationLease?> ClaimNextAsync(
        string workerIdentity,
        DateTimeOffset claimedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerIdentity);
        RequireUtc(claimedAtUtc, nameof(claimedAtUtc));
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var leaseToken = Guid.CreateVersion7();
        var leaseExpiresAtUtc = claimedAtUtc.Add(leaseDuration);
        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH candidate AS
                (
                    SELECT id
                    FROM catalog.publication_operation
                    WHERE
                        state = @pending
                        OR (state = @retry_wait AND next_attempt_at_utc <= @claimed_at_utc)
                        OR (state = @leased AND lease_expires_at_utc <= @claimed_at_utc)
                    ORDER BY created_at_utc, id
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                )
                UPDATE catalog.publication_operation AS operation
                SET
                    state = @leased,
                    attempt = operation.attempt + 1,
                    lease_token = @lease_token,
                    leased_by = @leased_by,
                    lease_expires_at_utc = @lease_expires_at_utc,
                    next_attempt_at_utc = NULL,
                    updated_at_utc = @claimed_at_utc
                FROM candidate
                WHERE operation.id = candidate.id
                RETURNING
                    operation.id,
                    operation.publication_id,
                    operation.publication_sequence,
                    operation.catalog_key,
                    operation.actor_id,
                    operation.request_document,
                    operation.request_digest,
                    operation.correlation_id,
                    operation.causation_id,
                    operation.created_at_utc,
                    operation.lease_token,
                    operation.attempt;
                """;
            command.Parameters.Add(new NpgsqlParameter<int>("pending", (int)CatalogPublicationOperationState.Pending));
            command.Parameters.Add(new NpgsqlParameter<int>("retry_wait", (int)CatalogPublicationOperationState.RetryWait));
            command.Parameters.Add(new NpgsqlParameter<int>("leased", (int)CatalogPublicationOperationState.Leased));
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("claimed_at_utc", claimedAtUtc));
            command.Parameters.Add(new NpgsqlParameter<Guid>("lease_token", leaseToken));
            command.Parameters.Add(new NpgsqlParameter<string>("leased_by", workerIdentity.Trim()));
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("lease_expires_at_utc", leaseExpiresAtUtc));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new CatalogPublicationOperationLease(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetGuid(4),
                reader.GetFieldValue<byte[]>(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.GetGuid(10),
                reader.GetInt32(11));
        }
        finally
        {
            if (shouldClose)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    public async Task ScheduleRetryAsync(
        Guid operationId,
        Guid leaseToken,
        CatalogPublicationOperationFailure failure,
        DateTimeOffset nextAttemptAtUtc,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        RequireLeaseIdentity(operationId, leaseToken);
        ArgumentNullException.ThrowIfNull(failure);
        RequireUtc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
        RequireUtc(updatedAtUtc, nameof(updatedAtUtc));
        if (nextAttemptAtUtc <= updatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextAttemptAtUtc),
                "Next attempt must be later than the failure timestamp.");
        }

        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE catalog.publication_operation
            SET
                state = {(int)CatalogPublicationOperationState.RetryWait},
                lease_token = NULL,
                leased_by = NULL,
                lease_expires_at_utc = NULL,
                next_attempt_at_utc = {nextAttemptAtUtc},
                failure_owner = {failure.Owner},
                failure_code = {failure.Code},
                failure_detail = {failure.Detail},
                failure_required_action = {failure.RequiredAction},
                updated_at_utc = {updatedAtUtc}
            WHERE id = {operationId}
              AND state = {(int)CatalogPublicationOperationState.Leased}
              AND lease_token = {leaseToken}
              AND lease_expires_at_utc > {updatedAtUtc};
            """, cancellationToken);
        EnsureLeaseMutation(affected, operationId);
    }

    public async Task FailAsync(
        Guid operationId,
        Guid leaseToken,
        CatalogPublicationOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken)
    {
        RequireLeaseIdentity(operationId, leaseToken);
        ArgumentNullException.ThrowIfNull(failure);
        RequireUtc(failedAtUtc, nameof(failedAtUtc));
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE catalog.publication_operation
            SET
                state = {(int)CatalogPublicationOperationState.Failed},
                lease_token = NULL,
                leased_by = NULL,
                lease_expires_at_utc = NULL,
                next_attempt_at_utc = NULL,
                failure_owner = {failure.Owner},
                failure_code = {failure.Code},
                failure_detail = {failure.Detail},
                failure_required_action = {failure.RequiredAction},
                updated_at_utc = {failedAtUtc}
            WHERE id = {operationId}
              AND state = {(int)CatalogPublicationOperationState.Leased}
              AND lease_token = {leaseToken}
              AND lease_expires_at_utc > {failedAtUtc};
            """, cancellationToken);
        EnsureLeaseMutation(affected, operationId);
    }

    private async Task<long> AllocatePublicationSequenceAsync(
        string catalogKey,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO catalog.publication_sequence (catalog_key, next_sequence)
                VALUES (@catalog_key, 2)
                ON CONFLICT (catalog_key)
                DO UPDATE SET next_sequence = catalog.publication_sequence.next_sequence + 1
                RETURNING next_sequence - 1;
                """;
            command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
            var result = await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "Publication sequence allocator returned no value for a durable operation.");
            return Convert.ToInt64(
                result,
                System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (shouldClose)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private static CatalogPublicationOperationSnapshot ToSnapshot(CatalogPublicationOperationRow row)
    {
        var state = Enum.IsDefined(typeof(CatalogPublicationOperationState), row.State)
            ? (CatalogPublicationOperationState)row.State
            : throw new InvalidOperationException(
                $"Catalog publication operation '{row.Id}' has unsupported state '{row.State}'.");
        var failureFields = new[]
        {
            row.FailureOwner,
            row.FailureCode,
            row.FailureDetail,
            row.FailureRequiredAction,
        };
        var populatedFailureFields = failureFields.Count(value => value is not null);
        if (populatedFailureFields is not 0 and not 4)
        {
            throw new InvalidOperationException(
                $"Catalog publication operation '{row.Id}' has a partial failure contract.");
        }

        return new CatalogPublicationOperationSnapshot(
            row.Id,
            row.PublicationId,
            row.PublicationSequence,
            row.CatalogKey,
            row.ActorId,
            state,
            row.Attempt,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.NextAttemptAtUtc,
            row.ResultPublicationId,
            populatedFailureFields == 0
                ? null
                : CatalogPublicationOperationFailure.Create(
                    row.FailureOwner!,
                    row.FailureCode!,
                    row.FailureDetail!,
                    row.FailureRequiredAction!));
    }

    private static void ValidateRegistration(CatalogPublicationOperationRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.OperationId == Guid.Empty ||
            registration.PublicationId == Guid.Empty ||
            registration.ActorId == Guid.Empty)
        {
            throw new ArgumentException("Operation and actor IDs are required.", nameof(registration));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(registration.CatalogKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.IdempotencyKey);
        ArgumentNullException.ThrowIfNull(registration.RequestDocument);
        if (registration.RequestDocument.Length == 0)
        {
            throw new ArgumentException("Publication request document is required.", nameof(registration));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(registration.RequestDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.CorrelationId);
        RequireUtc(registration.CreatedAtUtc, nameof(registration));
    }

    private static void RequireLeaseIdentity(Guid operationId, Guid leaseToken)
    {
        if (operationId == Guid.Empty || leaseToken == Guid.Empty)
        {
            throw new ArgumentException("Operation and lease token IDs are required.");
        }
    }

    private static void EnsureLeaseMutation(int affected, Guid operationId)
    {
        if (affected != 1)
        {
            throw new CatalogPublicationOperationLeaseLostException(operationId);
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be normalized to UTC.", parameterName);
        }
    }
}
