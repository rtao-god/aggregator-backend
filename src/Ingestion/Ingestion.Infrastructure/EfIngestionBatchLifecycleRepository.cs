using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>Persists one validated Ingestion aggregate transition and its exact replay result atomically.</summary>
public sealed class EfIngestionBatchLifecycleRepository : IIngestionBatchLifecycleRepository
{
    private readonly IngestionDbContext _dbContext;

    public EfIngestionBatchLifecycleRepository(IngestionDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public Task<IngestionBatchSnapshot?> ReadCommandResultAsync(
        IngestionCommandIdentity commandIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandIdentity);
        return ReadCommandResultCoreAsync(commandIdentity, cancellationToken);
    }

    public async Task<IngestionBatchCommandResult> SaveLifecycleAsync(
        ImportBatch batch,
        long expectedStoredAggregateRevision,
        IngestionCommandIdentity commandIdentity,
        string callerServiceIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(commandIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerServiceIdentity);
        if (expectedStoredAggregateRevision <= 0)
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_EXPECTED_REVISION_INVALID",
                500,
                "The lifecycle persistence owner received a non-positive stored revision.",
                "Correct the application command composition before retrying.");
        }

        if (batch.AggregateRevision != expectedStoredAggregateRevision + 1)
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_TRANSITION_REVISION_INVALID",
                500,
                "One lifecycle command must advance the aggregate revision exactly once.",
                "Execute one domain transition per persisted lifecycle command.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["batchId"] = batch.Id.Value,
                    ["expectedStoredAggregateRevision"] = expectedStoredAggregateRevision,
                    ["actualAggregateRevision"] = batch.AggregateRevision,
                });
        }

        var result = IngestionBatchSnapshot.From(batch);
        var resultDocument = IngestionCanonicalJson.Serialize(result);
        var resultDigest = IngestionCanonicalJson.ComputeDigest(resultDocument);
        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);
            var replay = await ReadCommandResultCoreAsync(commandIdentity, cancellationToken);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new IngestionBatchCommandResult(replay, true);
            }

            var row = await _dbContext.Batches
                .SingleOrDefaultAsync(candidate => candidate.Id == batch.Id.Value, cancellationToken)
                ?? throw BatchNotFound(batch.Id.Value);
            if (row.AggregateRevision != expectedStoredAggregateRevision)
            {
                throw RevisionConflict(
                    batch.Id.Value,
                    expectedStoredAggregateRevision,
                    row.AggregateRevision);
            }

            ValidateImmutableIdentity(row, batch);
            row.LastChangedAtUtc = batch.LastChangedAtUtc;
            row.State = (int)batch.State;
            row.AggregateRevision = batch.AggregateRevision;
            row.AcceptedItemCount = batch.AcceptedItemCount;
            row.ReviewRequiredItemCount = batch.ReviewRequiredItemCount;
            row.RejectedItemCount = batch.RejectedItemCount;
            row.FailureCode = batch.FailureCode;
            _dbContext.Commands.Add(new IngestionCommandRow
            {
                Scope = commandIdentity.Scope,
                Key = commandIdentity.Key,
                RequestDigest = commandIdentity.RequestDigest,
                BatchId = batch.Id.Value,
                ResultDocument = resultDocument,
                ResultDigest = resultDigest,
                CallerServiceIdentity = callerServiceIdentity,
                CreatedAtUtc = batch.LastChangedAtUtc,
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IngestionBatchCommandResult(result, false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            _dbContext.ChangeTracker.Clear();
            var actualRevision = await _dbContext.Batches
                .AsNoTracking()
                .Where(row => row.Id == batch.Id.Value)
                .Select(row => (long?)row.AggregateRevision)
                .SingleOrDefaultAsync(cancellationToken);
            throw RevisionConflict(
                batch.Id.Value,
                expectedStoredAggregateRevision,
                actualRevision,
                exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            _dbContext.ChangeTracker.Clear();
            var replay = await ReadCommandResultCoreAsync(commandIdentity, cancellationToken);
            if (replay is not null)
            {
                return new IngestionBatchCommandResult(replay, true);
            }

            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_LIFECYCLE_UNIQUENESS_CONFLICT",
                409,
                "The lifecycle command conflicts with an existing Ingestion-owned identity.",
                "Read the current batch and exact command result before retrying.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["batchId"] = batch.Id.Value,
                    ["scope"] = commandIdentity.Scope,
                    ["key"] = commandIdentity.Key,
                },
                exception);
        }
    }

    private async Task<IngestionBatchSnapshot?> ReadCommandResultCoreAsync(
        IngestionCommandIdentity commandIdentity,
        CancellationToken cancellationToken)
    {
        var command = await _dbContext.Commands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.Scope == commandIdentity.Scope && row.Key == commandIdentity.Key,
                cancellationToken);
        if (command is null)
        {
            return null;
        }

        if (!string.Equals(command.RequestDigest, commandIdentity.RequestDigest, StringComparison.Ordinal))
        {
            throw new IngestionApplicationException(
                "Ingestion.Commands",
                "INGESTION_IDEMPOTENCY_DIGEST_CONFLICT",
                409,
                "The Idempotency-Key was already used for a different command request.",
                "Reuse the key only with the exact original request or submit a new stable key.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["scope"] = commandIdentity.Scope,
                    ["key"] = commandIdentity.Key,
                    ["existingRequestDigest"] = command.RequestDigest,
                    ["actualRequestDigest"] = commandIdentity.RequestDigest,
                    ["batchId"] = command.BatchId,
                });
        }

        var batchExists = await _dbContext.Batches
            .AsNoTracking()
            .AnyAsync(row => row.Id == command.BatchId, cancellationToken);
        if (!batchExists)
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_IDEMPOTENCY_RESULT_MISSING",
                500,
                "An idempotency result references a missing import batch.",
                "Restore the Ingestion database from a verified backup.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["scope"] = commandIdentity.Scope,
                    ["key"] = commandIdentity.Key,
                    ["batchId"] = command.BatchId,
                });
        }

        var actualResultDigest = IngestionCanonicalJson.ComputeDigest(command.ResultDocument);
        if (!string.Equals(actualResultDigest, command.ResultDigest, StringComparison.Ordinal))
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_IDEMPOTENCY_RESULT_DIGEST_MISMATCH",
                500,
                "A persisted lifecycle command result failed its digest verification.",
                "Restore the command result from a verified Ingestion database backup.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["scope"] = commandIdentity.Scope,
                    ["key"] = commandIdentity.Key,
                    ["batchId"] = command.BatchId,
                    ["expectedResultDigest"] = command.ResultDigest,
                    ["actualResultDigest"] = actualResultDigest,
                });
        }

        var result = IngestionCanonicalJson.Deserialize<IngestionBatchSnapshot>(command.ResultDocument);
        if (result.Id.Value != command.BatchId)
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_IDEMPOTENCY_RESULT_IDENTITY_MISMATCH",
                500,
                "A persisted lifecycle result identifies a different import batch.",
                "Restore the command result from a verified Ingestion database backup.");
        }

        return result;
    }

    private static void ValidateImmutableIdentity(ImportBatchRow row, ImportBatch batch)
    {
        var consistent = row.ProducerIdentity == batch.ProducerIdentity &&
            row.ProducerBuild == batch.ProducerBuild &&
            row.CollectorExportId == batch.CollectorExportId &&
            row.CollectorExportDigest == batch.CollectorExportDigest &&
            row.TargetSiteKey == batch.TargetSiteKey &&
            row.TargetCatalogKey == batch.TargetCatalogKey &&
            row.TargetCatalogConfigurationRevisionId == batch.TargetCatalogConfigurationRevisionId &&
            row.ExpectedItemCount == batch.ExpectedItemCount &&
            row.ManifestDigest == batch.ManifestDigest &&
            row.ItemIndexDigest == batch.ItemIndexDigest &&
            row.PayloadDigest == batch.PayloadDigest &&
            row.PayloadObjectKey == batch.PayloadObjectKey &&
            row.PayloadObjectDigest == batch.PayloadObjectDigest &&
            row.PayloadObjectSize == batch.PayloadObjectSize &&
            row.PayloadContentType == batch.PayloadContentType &&
            row.RegisteredAtUtc == batch.RegisteredAtUtc;
        if (!consistent)
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_BATCH_IDENTITY_MISMATCH",
                500,
                "The lifecycle aggregate does not match the immutable persisted batch identity.",
                "Reload the exact batch snapshot before applying a domain transition.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["batchId"] = batch.Id.Value,
                    ["storedAggregateRevision"] = row.AggregateRevision,
                    ["actualAggregateRevision"] = batch.AggregateRevision,
                });
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        };

    private static IngestionApplicationException BatchNotFound(Guid batchId) =>
        new(
            "Ingestion.Batches",
            "INGESTION_BATCH_NOT_FOUND",
            404,
            $"Import batch '{batchId:D}' was not found.",
            "Use the exact ImportBatchId returned by registration.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["batchId"] = batchId,
            });

    private static IngestionApplicationException RevisionConflict(
        Guid batchId,
        long expectedRevision,
        long? actualRevision,
        Exception? innerException = null) =>
        new(
            "Ingestion.Batches",
            "INGESTION_BATCH_REVISION_CONFLICT",
            409,
            "The import batch changed before the lifecycle command was persisted.",
            "Reload the exact batch and retry with its current aggregate revision.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["batchId"] = batchId,
                ["expectedRevision"] = expectedRevision,
                ["actualRevision"] = actualRevision,
            },
            innerException);
}
