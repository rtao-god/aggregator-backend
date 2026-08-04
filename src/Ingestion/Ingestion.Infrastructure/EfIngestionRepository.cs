using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>Persists one exact import package and its idempotency result in the Ingestion database.</summary>
public sealed class EfIngestionRepository : IIngestionBatchRepository
{
    private readonly IngestionDbContext _dbContext;

    public EfIngestionRepository(IngestionDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IngestionBatchRegistrationResult> RegisterAsync(
        ImportBatch batch,
        AggregatorCandidateIngestionManifest manifest,
        IngestionCommandIdentity commandIdentity,
        string callerServiceIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(commandIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerServiceIdentity);
        var canonicalManifest = IngestionCanonicalJson.Serialize(manifest);
        var canonicalManifestDigest = IngestionCanonicalJson.ComputeDigest(canonicalManifest);
        if (!string.Equals(canonicalManifestDigest, batch.ManifestDigest, StringComparison.Ordinal))
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_MANIFEST_DIGEST_INCONSISTENT",
                500,
                "The validated import batch and canonical manifest have different digests.",
                "Correct the registration composition before persisting the package.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["batchId"] = batch.Id.Value,
                    ["batchManifestDigest"] = batch.ManifestDigest,
                    ["canonicalManifestDigest"] = canonicalManifestDigest,
                });
        }

        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);
            var replay = await TryReadReplayAsync(commandIdentity, cancellationToken);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }

            var existingExport = await _dbContext.Batches
                .AsNoTracking()
                .Where(row =>
                    row.ProducerIdentity == batch.ProducerIdentity &&
                    row.CollectorExportId == batch.CollectorExportId)
                .Select(row => new
                {
                    row.Id,
                    row.ManifestDigest,
                    row.TargetCatalogKey,
                    row.TargetCatalogConfigurationRevisionId,
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (existingExport is not null)
            {
                throw ExportAlreadyRegistered(batch, existingExport.Id, existingExport.ManifestDigest);
            }

            _dbContext.Batches.Add(ToRow(batch));
            _dbContext.Manifests.Add(new ImportBatchManifestRow
            {
                BatchId = batch.Id.Value,
                ContractIdentity = manifest.ContractIdentity,
                ContractRevision = manifest.ContractRevision,
                CanonicalDocument = canonicalManifest,
                ContentDigest = canonicalManifestDigest,
                CreatedAtUtc = manifest.CreatedAtUtc,
            });
            foreach (var sourcePolicy in manifest.SourcePolicies)
            {
                _dbContext.SourcePolicies.Add(new ImportBatchSourcePolicyRow
                {
                    BatchId = batch.Id.Value,
                    SourceKey = sourcePolicy.SourceKey,
                    PolicyDigest = sourcePolicy.PolicyDigest,
                    UsagePolicy = (int)sourcePolicy.UsagePolicy,
                });
            }

            foreach (var artifact in manifest.Artifacts)
            {
                _dbContext.Artifacts.Add(new ImportBatchArtifactRow
                {
                    BatchId = batch.Id.Value,
                    Role = (int)artifact.Role,
                    ObjectKey = artifact.ObjectKey,
                    ContentDigest = artifact.ContentDigest,
                    Size = artifact.Size,
                    ContentType = artifact.ContentType,
                });
            }

            _dbContext.Commands.Add(new IngestionCommandRow
            {
                Scope = commandIdentity.Scope,
                Key = commandIdentity.Key,
                RequestDigest = commandIdentity.RequestDigest,
                BatchId = batch.Id.Value,
                CallerServiceIdentity = callerServiceIdentity,
                CreatedAtUtc = batch.RegisteredAtUtc,
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IngestionBatchRegistrationResult(IngestionBatchSnapshot.From(batch), false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            _dbContext.ChangeTracker.Clear();
            var replay = await TryReadReplayAsync(commandIdentity, cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            var existingExport = await _dbContext.Batches
                .AsNoTracking()
                .Where(row =>
                    row.ProducerIdentity == batch.ProducerIdentity &&
                    row.CollectorExportId == batch.CollectorExportId)
                .Select(row => new { row.Id, row.ManifestDigest })
                .SingleOrDefaultAsync(cancellationToken);
            if (existingExport is not null)
            {
                throw ExportAlreadyRegistered(
                    batch,
                    existingExport.Id,
                    existingExport.ManifestDigest,
                    exception);
            }

            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_REGISTRATION_UNIQUENESS_CONFLICT",
                409,
                "The import batch registration conflicts with an existing Ingestion-owned identity.",
                "Read the existing batch or submit a new collector export identity.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["batchId"] = batch.Id.Value,
                    ["producerIdentity"] = batch.ProducerIdentity,
                    ["collectorExportId"] = batch.CollectorExportId,
                    ["idempotencyScope"] = commandIdentity.Scope,
                    ["idempotencyKey"] = commandIdentity.Key,
                },
                exception);
        }
    }

    public async Task<IngestionBatchSnapshot?> ReadAsync(
        ImportBatchId batchId,
        CancellationToken cancellationToken)
    {
        if (batchId.Value == Guid.Empty)
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_BATCH_ID_REQUIRED",
                400,
                "A non-empty import batch ID is required.",
                "Provide the exact ImportBatchId returned by registration.");
        }

        var row = await _dbContext.Batches
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == batchId.Value, cancellationToken);
        return row is null ? null : ToSnapshot(row);
    }

    private async Task<IngestionBatchRegistrationResult?> TryReadReplayAsync(
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
                "The Idempotency-Key was already used for a different registration request.",
                "Reuse the key only with the exact original request or submit a new stable key.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["scope"] = commandIdentity.Scope,
                    ["key"] = commandIdentity.Key,
                    ["existingRequestDigest"] = command.RequestDigest,
                    ["actualRequestDigest"] = commandIdentity.RequestDigest,
                    ["existingBatchId"] = command.BatchId,
                });
        }

        var batch = await _dbContext.Batches
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == command.BatchId, cancellationToken)
            ?? throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_IDEMPOTENCY_RESULT_MISSING",
                500,
                "An idempotency record references a missing import batch.",
                "Repair the Ingestion database through an owner migration or restore operation.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["scope"] = commandIdentity.Scope,
                    ["key"] = commandIdentity.Key,
                    ["batchId"] = command.BatchId,
                });
        return new IngestionBatchRegistrationResult(ToSnapshot(batch), true);
    }

    private static ImportBatchRow ToRow(ImportBatch batch) =>
        new()
        {
            Id = batch.Id.Value,
            ProducerIdentity = batch.ProducerIdentity,
            ProducerBuild = batch.ProducerBuild,
            CollectorExportId = batch.CollectorExportId,
            CollectorExportDigest = batch.CollectorExportDigest,
            TargetSiteKey = batch.TargetSiteKey,
            TargetCatalogKey = batch.TargetCatalogKey,
            TargetCatalogConfigurationRevisionId = batch.TargetCatalogConfigurationRevisionId,
            ExpectedItemCount = batch.ExpectedItemCount,
            ManifestDigest = batch.ManifestDigest,
            ItemIndexDigest = batch.ItemIndexDigest,
            PayloadDigest = batch.PayloadDigest,
            PayloadObjectKey = batch.PayloadObjectKey,
            PayloadObjectDigest = batch.PayloadObjectDigest,
            PayloadObjectSize = batch.PayloadObjectSize,
            PayloadContentType = batch.PayloadContentType,
            RegisteredAtUtc = batch.RegisteredAtUtc,
            LastChangedAtUtc = batch.LastChangedAtUtc,
            State = (int)batch.State,
            AggregateRevision = batch.AggregateRevision,
            AcceptedItemCount = batch.AcceptedItemCount,
            ReviewRequiredItemCount = batch.ReviewRequiredItemCount,
            RejectedItemCount = batch.RejectedItemCount,
            FailureCode = batch.FailureCode,
        };

    private static IngestionBatchSnapshot ToSnapshot(ImportBatchRow row)
    {
        if (!Enum.IsDefined(typeof(ImportBatchState), row.State))
        {
            throw new IngestionApplicationException(
                "Ingestion.Persistence",
                "INGESTION_BATCH_STATE_CORRUPT",
                500,
                $"Import batch '{row.Id}' contains unsupported state value '{row.State}'.",
                "Repair the row through an owner migration or restore operation.");
        }

        return new IngestionBatchSnapshot(
            ImportBatchId.Create(row.Id),
            row.ProducerIdentity,
            row.ProducerBuild,
            row.CollectorExportId,
            row.CollectorExportDigest,
            row.TargetSiteKey,
            row.TargetCatalogKey,
            row.TargetCatalogConfigurationRevisionId,
            row.ExpectedItemCount,
            row.ManifestDigest,
            row.ItemIndexDigest,
            row.PayloadDigest,
            row.PayloadObjectKey,
            row.PayloadObjectDigest,
            row.PayloadObjectSize,
            row.PayloadContentType,
            row.RegisteredAtUtc,
            row.LastChangedAtUtc,
            (ImportBatchState)row.State,
            row.AggregateRevision,
            row.AcceptedItemCount,
            row.ReviewRequiredItemCount,
            row.RejectedItemCount,
            row.FailureCode);
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        };

    private static IngestionApplicationException ExportAlreadyRegistered(
        ImportBatch batch,
        Guid existingBatchId,
        string existingManifestDigest,
        Exception? innerException = null) =>
        new(
            "Ingestion.Batches",
            "INGESTION_COLLECTOR_EXPORT_ALREADY_REGISTERED",
            409,
            "The producer and collector export identity is already registered as another import batch.",
            "Read the existing import batch instead of registering the collector export again.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["producerIdentity"] = batch.ProducerIdentity,
                ["collectorExportId"] = batch.CollectorExportId,
                ["existingBatchId"] = existingBatchId,
                ["existingManifestDigest"] = existingManifestDigest,
                ["actualManifestDigest"] = batch.ManifestDigest,
            },
            innerException);
}
