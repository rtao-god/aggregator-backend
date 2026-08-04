
namespace Aggregator.CatalogMedia.Contracts;

public static class CatalogMediaContractIdentity
{
    public const string CommandApi = "aggregator-catalog-media";
    public const int CommandApiRevision = 1;
}

public enum CatalogMediaStateContract
{
    Registered = 1,
    UploadAuthorized = 2,
    Uploaded = 3,
    Scanning = 4,
    Accepted = 5,
    Rejected = 6,
    RightsRevoked = 7,
    Archived = 8,
}

public enum CatalogMediaRightsBasisContract
{
    OwnerProvided = 1,
    Licensed = 2,
    PublicDomain = 3,
}

public enum CatalogMediaVariantKindContract
{
    Original = 1,
    Thumbnail = 2,
    Card = 3,
    Gallery = 4,
}

public sealed record RegisterCatalogMediaRequest(
    string ContractIdentity,
    int ContractRevision,
    string CatalogKey,
    string ContentType,
    string ContentDigest,
    long Size,
    CatalogMediaRightsBasisContract RightsBasis,
    string RightsReference);

public sealed record PrepareCatalogMediaUploadRequest(
    long ExpectedAggregateRevision,
    int LifetimeSeconds);

public sealed record CompleteCatalogMediaUploadRequest(long ExpectedAggregateRevision);

public sealed record RevokeCatalogMediaRightsRequest(
    long ExpectedAggregateRevision,
    string Reason);

public sealed record CatalogMediaVariantResponse(
    Guid Id,
    CatalogMediaVariantKindContract Kind,
    string ObjectKey,
    string ContentType,
    string ContentDigest,
    long Size,
    int Width,
    int Height,
    DateTimeOffset CreatedAtUtc);

public sealed record CatalogMediaResponse(
    Guid Id,
    string CatalogKey,
    CatalogMediaStateContract State,
    string QuarantineObjectKey,
    string ExpectedContentType,
    string ExpectedContentDigest,
    long ExpectedSize,
    CatalogMediaRightsBasisContract RightsBasis,
    string RightsReference,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset ChangedAtUtc,
    long AggregateRevision,
    DateTimeOffset? UploadAuthorizationExpiresAtUtc,
    DateTimeOffset? UploadedAtUtc,
    DateTimeOffset? ScannedAtUtc,
    DateTimeOffset? AcceptedAtUtc,
    DateTimeOffset? RightsRevokedAtUtc,
    string? FailureCode,
    IReadOnlyList<CatalogMediaVariantResponse> Variants);

public sealed record CatalogMediaUploadAuthorizationResponse(
    CatalogMediaResponse Asset,
    Uri UploadUri,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyDictionary<string, string> RequiredHeaders);

public static class CatalogMediaIntegrationEventTypes
{
    public const string Accepted = "catalog.media.accepted";
    public const string Rejected = "catalog.media.rejected";
    public const string RightsRevoked = "catalog.media.rights-revoked";
}

public static class CatalogMediaIntegrationEventContracts
{
    public const string Accepted = "aggregator.catalog-media.accepted@1";
    public const string Rejected = "aggregator.catalog-media.rejected@1";
    public const string RightsRevoked = "aggregator.catalog-media.rights-revoked@1";
}

public sealed record CatalogMediaAccepted(
    Guid EventId,
    Guid AssetId,
    string CatalogKey,
    long AggregateRevision,
    IReadOnlyList<CatalogMediaVariantResponse> Variants,
    DateTimeOffset OccurredAtUtc);

public sealed record CatalogMediaRejected(
    Guid EventId,
    Guid AssetId,
    string CatalogKey,
    long AggregateRevision,
    string FailureCode,
    DateTimeOffset OccurredAtUtc);

public sealed record CatalogMediaRightsRevoked(
    Guid EventId,
    Guid AssetId,
    string CatalogKey,
    long AggregateRevision,
    DateTimeOffset OccurredAtUtc);
