internal static class CatalogMediaRepositoryTemplateWriter
{
    public static void Write(CatalogMediaGenerationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        File.WriteAllText(
            Path.Combine(context.InfrastructureDirectory, "EfCatalogMediaRepository.cs"),
            Repository().Trim() + Environment.NewLine);
    }

    private static string Repository() =>
        """
        using System.Data;
        using Aggregator.CatalogMedia.Application;
        using Aggregator.CatalogMedia.Domain;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore.Storage;
        using Npgsql;
        using NpgsqlTypes;

        namespace Aggregator.CatalogMedia.Infrastructure;

        public sealed class EfCatalogMediaRepository(CatalogMediaDbContext dbContext) : ICatalogMediaRepository
        {
            public async Task<CatalogMediaCommandResult?> ReadCommandResultAsync(
                CatalogMediaCommandIdentity commandIdentity,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(commandIdentity);
                var row = await dbContext.Commands.AsNoTracking().SingleOrDefaultAsync(
                    candidate => candidate.Scope == commandIdentity.Scope &&
                        candidate.IdempotencyKey == commandIdentity.Key,
                    cancellationToken);
                return row is null ? null : RestoreCommandResult(row, commandIdentity);
            }

            public Task<CatalogMediaCommandResult> AddAsync(
                CatalogMediaAsset asset,
                CatalogMediaCommandIdentity commandIdentity,
                CatalogMediaCommandContext context,
                CancellationToken cancellationToken) =>
                ExecuteCommandAsync(
                    commandIdentity,
                    context,
                    async innerCancellationToken =>
                    {
                        ArgumentNullException.ThrowIfNull(asset);
                        dbContext.Assets.Add(ToRow(asset));
                        await AddCommandResultAsync(asset, commandIdentity, context, innerCancellationToken);
                        return asset;
                    },
                    cancellationToken);

            public Task<CatalogMediaCommandResult> SaveAsync(
                CatalogMediaAsset asset,
                long expectedStoredAggregateRevision,
                CatalogMediaCommandIdentity commandIdentity,
                CatalogMediaCommandContext context,
                CatalogMediaOutboxMessage? outbox,
                CancellationToken cancellationToken) =>
                ExecuteCommandAsync(
                    commandIdentity,
                    context,
                    async innerCancellationToken =>
                    {
                        ArgumentNullException.ThrowIfNull(asset);
                        var row = await dbContext.Assets.SingleOrDefaultAsync(
                            candidate => candidate.Id == asset.Id,
                            innerCancellationToken)
                            ?? throw Failure(
                                "CATALOG_MEDIA_NOT_FOUND",
                                $"Catalog media asset '{asset.Id}' was not found.",
                                "Reload the exact media asset before retrying the command.",
                                404);
                        EnsureStoredRevision(
                            row.AggregateRevision,
                            expectedStoredAggregateRevision,
                            asset.Id);
                        Apply(row, asset);
                        await ReplaceVariantsAsync(asset, innerCancellationToken);
                        if (outbox is not null) AddOutbox(outbox);
                        await AddCommandResultAsync(asset, commandIdentity, context, innerCancellationToken);
                        return asset;
                    },
                    cancellationToken);

            public async Task<CatalogMediaAsset?> GetAsync(
                Guid assetId,
                CancellationToken cancellationToken)
            {
                var row = await dbContext.Assets.AsNoTracking().SingleOrDefaultAsync(
                    candidate => candidate.Id == assetId,
                    cancellationToken);
                if (row is null) return null;
                var variants = await dbContext.Variants.AsNoTracking()
                    .Where(candidate => candidate.AssetId == assetId)
                    .OrderBy(candidate => candidate.Kind)
                    .ToArrayAsync(cancellationToken);
                return Restore(row, variants);
            }

            public async Task<CatalogMediaProcessingLease?> TryLeaseUploadedAsync(
                string workerIdentity,
                DateTimeOffset nowUtc,
                TimeSpan leaseDuration,
                int maximumAttempts,
                CancellationToken cancellationToken)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(workerIdentity);
                RequireUtc(nowUtc, nameof(nowUtc));
                if (workerIdentity.Length > 200 || workerIdentity.Any(char.IsControl))
                    throw new ArgumentException("Catalog media worker identity is invalid.", nameof(workerIdentity));
                if (leaseDuration < TimeSpan.FromSeconds(10) || leaseDuration > TimeSpan.FromMinutes(30))
                    throw new ArgumentOutOfRangeException(nameof(leaseDuration));
                if (maximumAttempts is < 1 or > 100)
                    throw new ArgumentOutOfRangeException(nameof(maximumAttempts));

                await using var transaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
                var leaseToken = Guid.CreateVersion7();
                var leaseExpiresAtUtc = nowUtc.Add(leaseDuration);
                var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                await using var command = connection.CreateCommand();
                command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
                command.CommandText = """
                    private static WITH candidate private AS
                    (
                        SELECT asset.id
                        FROM media.asset AS asset
                        LEFT JOIN operations.processing_work AS work ON work.asset_id = asset.id
                        WHERE asset.state = @uploaded_state
                          AND work.completed_at_utc IS NULL
                          AND (work.lease_expires_at_utc IS NULL OR work.lease_expires_at_utc <= @now_utc)
                          private AND COALESCE(work.attempt_count, 0) < private static @maximum_attempts
                        ORDER BY asset.uploaded_at_utc, asset.id
                        FOR UPDATE OF asset SKIP LOCKED
                        LIMIT 1
                    )
                    private static INSERT INTO operations.processing_work
                        (asset_id, lease_token, leased_by, lease_expires_at_utc, attempt_count,
                         last_error, last_failed_at_utc, completed_at_utc)
                    SELECT candidate.id, @lease_token, @leased_by, @lease_expires_at_utc,
                           COALESCE(existing.attempt_count, 0), existing.last_error,
                           existing.last_failed_at_utc, private static NULL
                    FROM private static candidate
                    LEFT JOIN operations.processing_work AS existing ON existing.asset_id = candidate.id
                    ON CONFLICT (asset_id)
                    DO UPDATE SET
                        lease_token = EXCLUDED.lease_token,
                        leased_by = EXCLUDED.leased_by,
                        lease_expires_at_utc = EXCLUDED.lease_expires_at_utc
                    WHERE operations.processing_work.completed_at_utc IS NULL
                      AND (operations.processing_work.lease_expires_at_utc IS NULL
                           OR operations.processing_work.lease_expires_at_utc <= @now_utc)
                      AND operations.processing_work.attempt_count<@maximum_attempts
                    RETURNING asset_id, attempt_count;
                    """;
                command.Parameters.AddWithValue("uploaded_state", NpgsqlDbType.Integer, (int) CatalogMediaState.Uploaded);
    command.Parameters.AddWithValue("now_utc", NpgsqlDbType.TimestampTz, nowUtc);
                command.Parameters.AddWithValue("maximum_attempts", NpgsqlDbType.Integer, maximumAttempts);
                command.Parameters.AddWithValue("lease_token", NpgsqlDbType.Uuid, leaseToken);
                command.Parameters.AddWithValue("leased_by", NpgsqlDbType.Text, workerIdentity.Trim());
                command.Parameters.AddWithValue("lease_expires_at_utc", NpgsqlDbType.TimestampTz, leaseExpiresAtUtc);
                private static Guid? assetId = null;
    private static var attemptCount = 0;
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
{
    if (await reader.ReadAsync(cancellationToken))
    {
        assetId = reader.GetGuid(0);
        attemptCount = reader.GetInt32(1);
    }
}
if (assetId is null)
{
    await transaction.CommitAsync(cancellationToken);
    return null;
}

var row = await dbContext.Assets.SingleAsync(
    candidate => candidate.Id == assetId.Value,
    cancellationToken);
var variants = await dbContext.Variants.AsNoTracking()
    .Where(candidate => candidate.AssetId == assetId.Value)
    .ToArrayAsync(cancellationToken);
var asset = Restore(row, variants);
asset.StartScan(asset.AggregateRevision, nowUtc);
Apply(row, asset);
await dbContext.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);
return new CatalogMediaProcessingLease(
    asset.Id,
    leaseToken,
    attemptCount,
    leaseExpiresAtUtc,
    asset.AggregateRevision,
    asset);
            }

private static void Apply(CatalogMediaAssetRow row, CatalogMediaAsset asset)
{
    if (row.Id != asset.Id ||
        !string.Equals(row.CatalogKey, asset.CatalogKey, StringComparison.Ordinal) ||
        !string.Equals(row.QuarantineObjectKey, asset.QuarantineObjectKey, StringComparison.Ordinal) ||
        !string.Equals(row.ExpectedContentDigest, asset.ExpectedContentDigest, StringComparison.Ordinal) ||
        row.ExpectedSize != asset.ExpectedSize ||
        row.RightsBasis != (int)asset.RightsBasis ||
        row.RegisteredAtUtc != asset.RegisteredAtUtc)
    {
        throw Failure(
            "CATALOG_MEDIA_IMMUTABLE_IDENTITY_MISMATCH",
            "Media aggregate does not match its immutable persisted registration identity.",
            "Reload the exact asset before applying a transition.");
    }
    row.State = (int)asset.State;
    row.RightsReference = asset.RightsReference;
    row.ChangedAtUtc = asset.ChangedAtUtc;
    row.AggregateRevision = asset.AggregateRevision;
    row.UploadAuthorizationExpiresAtUtc = asset.UploadAuthorizationExpiresAtUtc;
    row.UploadedAtUtc = asset.UploadedAtUtc;
    row.ScannedAtUtc = asset.ScannedAtUtc;
    row.AcceptedAtUtc = asset.AcceptedAtUtc;
    row.RightsRevokedAtUtc = asset.RightsRevokedAtUtc;
    row.RightsRevokedByActorId = asset.RightsRevokedByActorId;
    row.FailureCode = asset.FailureCode;
}

private static CatalogMediaAsset Restore(
    CatalogMediaAssetRow row,
    IEnumerable<CatalogMediaVariantRow> variants) =>
    CatalogMediaAsset.Restore(
        row.Id,
        row.CatalogKey,
        (CatalogMediaState)row.State,
        row.QuarantineObjectKey,
        row.ExpectedContentType,
        row.ExpectedContentDigest,
        row.ExpectedSize,
        (CatalogMediaRightsBasis)row.RightsBasis,
        row.RightsReference,
        row.RegisteredAtUtc,
        row.ChangedAtUtc,
        row.AggregateRevision,
        row.UploadAuthorizationExpiresAtUtc,
        row.UploadedAtUtc,
        row.ScannedAtUtc,
        row.AcceptedAtUtc,
        row.RightsRevokedAtUtc,
        row.RightsRevokedByActorId,
        row.FailureCode,
        variants.Select(variant => CatalogMediaVariant.Create(
            variant.Id,
            variant.AssetId,
            (CatalogMediaVariantKind)variant.Kind,
            variant.ObjectKey,
            variant.ContentType,
            variant.ContentDigest,
            variant.Size,
            variant.Width,
            variant.Height,
            variant.CreatedAtUtc)));

private static CatalogMediaApplicationException StaleLease(Guid assetId) =>
    Failure(
        "CATALOG_MEDIA_STALE_PROCESSING_LEASE",
        $"Media processing lease for asset '{assetId}' is no longer current.",
        "Discard the worker result and reacquire the asset.",
        409);

private static CatalogMediaApplicationException Failure(
    string code,
    string message,
    string action,
    int status = 500,
    Exception? innerException = null) =>
    new("CatalogMedia.Persistence", code, status, message, action, innerException: innerException);
        }
        """;
}
