namespace Aggregator.Catalog.Contracts;

public static class CatalogIntegrationEventTypes
{
    public const string PublicationActivated = "catalog.publication.activated";
    public const string ListingClaimVerified = "catalog.listing-claim.verified";
    public const string ListingClaimRevoked = "catalog.listing-claim.revoked";
}

public static class CatalogIntegrationEventContracts
{
    public const string PublicationActivated = "aggregator.catalog.publication-activated@1";
    public const string ListingClaimVerified = "aggregator.catalog.listing-claim-verified@1";
    public const string ListingClaimRevoked = "aggregator.catalog.listing-claim-revoked@1";
}

public enum PublicationActivationKindContract
{
    Publication = 1,
    Rollback = 2,
}

public sealed record CatalogPublicationActivated(
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

public sealed record CatalogListingClaimVerified(
    Guid EventId,
    Guid ClaimId,
    Guid GrantId,
    Guid ListingId,
    Guid ActorId,
    IReadOnlyList<ListingAccessScopeContract> Scopes,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset OccurredAtUtc);

public sealed record CatalogListingClaimRevoked(
    Guid EventId,
    Guid ClaimId,
    Guid ListingId,
    Guid ActorId,
    DateTimeOffset OccurredAtUtc);
