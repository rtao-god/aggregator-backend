namespace Aggregator.Catalog.Contracts;

public static class CatalogIntegrationEventTypes
{
    public const string PublicationActivatedV1 = "catalog.publication-activated.v1";
    public const string ListingClaimVerifiedV1 = "catalog.listing-claim-verified.v1";
    public const string ListingClaimRevokedV1 = "catalog.listing-claim-revoked.v1";
}

public enum PublicationActivationKindContract
{
    Publication = 1,
    Rollback = 2,
}

public sealed record CatalogPublicationActivatedV1(
    Guid EventId,
    Guid PublicationId,
    string CatalogKey,
    Guid ConfigurationRevisionId,
    long PublicationSequence,
    string ArtifactKey,
    string ArtifactDigest,
    PublicationActivationKindContract ActivationKind,
    Guid? PreviousPublicationId,
    DateTimeOffset OccurredAtUtc);

public sealed record CatalogListingClaimVerifiedV1(
    Guid EventId,
    Guid ClaimId,
    Guid GrantId,
    Guid ListingId,
    Guid ActorId,
    IReadOnlyList<ListingAccessScopeContract> Scopes,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset OccurredAtUtc);

public sealed record CatalogListingClaimRevokedV1(
    Guid EventId,
    Guid ClaimId,
    Guid ListingId,
    Guid ActorId,
    DateTimeOffset OccurredAtUtc);
