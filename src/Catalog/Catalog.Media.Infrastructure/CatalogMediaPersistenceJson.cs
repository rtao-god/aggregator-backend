using Aggregator.Catalog.Media.Application;
using Aggregator.Catalog.Media.Domain;

namespace Aggregator.Catalog.Media.Infrastructure;

internal sealed record CatalogMediaPersistenceVariant(
    Guid Id,
    Guid AssetId,
    CatalogMediaVariantKind Kind,
    string ObjectKey,
    string ContentType,
    string ContentDigest,
    long Size,
    int Width,
    int Height,
    DateTimeOffset CreatedAtUtc);

internal sealed record CatalogMediaPersistenceSnapshot(
    Guid Id,
    string CatalogKey,
    CatalogMediaState State,
    string QuarantineObjectKey,
    string ExpectedContentType,
    string ExpectedContentDigest,
    long ExpectedSize,
    CatalogMediaRightsBasis RightsBasis,
    string RightsReference,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset ChangedAtUtc,
    long AggregateRevision,
    DateTimeOffset? UploadAuthorizationExpiresAtUtc,
    DateTimeOffset? UploadedAtUtc,
    DateTimeOffset? ScannedAtUtc,
    DateTimeOffset? AcceptedAtUtc,
    DateTimeOffset? RightsRevokedAtUtc,
    Guid? RightsRevokedByActorId,
    string? FailureCode,
    IReadOnlyList<CatalogMediaPersistenceVariant> Variants);

internal static class CatalogMediaPersistenceJson
{
    public static byte[] Serialize(CatalogMediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return CatalogMediaCanonicalJson.Serialize(ToSnapshot(asset));
    }

    public static CatalogMediaAsset Deserialize(ReadOnlySpan<byte> document, string expectedDigest)
    {
        CatalogMediaCanonicalJson.RequireDigest(expectedDigest, nameof(expectedDigest));
        var actualDigest = CatalogMediaCanonicalJson.ComputeDigest(document);
        if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
        {
            throw Failure(
                "CATALOG_MEDIA_COMMAND_RESULT_DIGEST_MISMATCH",
                "Persisted Catalog media command result failed digest verification.",
                "Restore the exact command result from a verified catalog_db backup.");
        }
        return Restore(CatalogMediaCanonicalJson.Deserialize<CatalogMediaPersistenceSnapshot>(document));
    }

    public static CatalogMediaAsset Restore(CatalogMediaPersistenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var variants = snapshot.Variants.Select(item => CatalogMediaVariant.Create(
            item.Id, item.AssetId, item.Kind, item.ObjectKey, item.ContentType,
            item.ContentDigest, item.Size, item.Width, item.Height, item.CreatedAtUtc));
        return CatalogMediaAsset.Restore(
            snapshot.Id,
            snapshot.CatalogKey,
            snapshot.State,
            snapshot.QuarantineObjectKey,
            snapshot.ExpectedContentType,
            snapshot.ExpectedContentDigest,
            snapshot.ExpectedSize,
            snapshot.RightsBasis,
            snapshot.RightsReference,
            snapshot.RegisteredAtUtc,
            snapshot.ChangedAtUtc,
            snapshot.AggregateRevision,
            snapshot.UploadAuthorizationExpiresAtUtc,
            snapshot.UploadedAtUtc,
            snapshot.ScannedAtUtc,
            snapshot.AcceptedAtUtc,
            snapshot.RightsRevokedAtUtc,
            snapshot.RightsRevokedByActorId,
            snapshot.FailureCode,
            variants);
    }

    public static CatalogMediaPersistenceSnapshot ToSnapshot(CatalogMediaAsset asset) =>
        new(
            asset.Id,
            asset.CatalogKey,
            asset.State,
            asset.QuarantineObjectKey,
            asset.ExpectedContentType,
            asset.ExpectedContentDigest,
            asset.ExpectedSize,
            asset.RightsBasis,
            asset.RightsReference,
            asset.RegisteredAtUtc,
            asset.ChangedAtUtc,
            asset.AggregateRevision,
            asset.UploadAuthorizationExpiresAtUtc,
            asset.UploadedAtUtc,
            asset.ScannedAtUtc,
            asset.AcceptedAtUtc,
            asset.RightsRevokedAtUtc,
            asset.RightsRevokedByActorId,
            asset.FailureCode,
            asset.Variants.Select(item => new CatalogMediaPersistenceVariant(
                item.Id, item.AssetId, item.Kind, item.ObjectKey, item.ContentType,
                item.ContentDigest, item.Size, item.Width, item.Height, item.CreatedAtUtc)).ToArray());

    private static CatalogMediaApplicationException Failure(string code, string message, string action) =>
        new("Catalog.Media.Persistence", code, 500, message, action);
}
