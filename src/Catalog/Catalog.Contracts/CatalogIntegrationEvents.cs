namespace Aggregator.Catalog.Contracts;

public static class CatalogIntegrationEventTypes
{
    public const string ConfigurationActivated = "catalog.configuration.activated";
    public const string PublicationActivated = "catalog.publication.activated";
    public const string ListingClaimVerified = "catalog.listing-claim.verified";
    public const string ListingClaimRevoked = "catalog.listing-claim.revoked";
    public const string PublicVisibilitySuppressionChanged =
        "catalog.public-visibility-suppression.changed";
}

public static class CatalogIntegrationEventContracts
{
    public const string ConfigurationActivated = "aggregator.catalog.configuration-activated@1";
    public const string PublicationActivated = "aggregator.catalog.publication-activated@2";
    public const string ListingClaimVerified = "aggregator.catalog.listing-claim-verified@1";
    public const string ListingClaimRevoked = "aggregator.catalog.listing-claim-revoked@1";
    public const string PublicVisibilitySuppressionChanged =
        "aggregator.catalog.public-visibility-suppression-changed@1";
}

/// <summary>
/// Minimal active product-configuration snapshot consumed by bounded contexts that validate Catalog identity.
/// </summary>
public sealed record CatalogConfigurationActivated(
    Guid EventId,
    string SiteKey,
    string CatalogKey,
    Guid ConfigurationRevisionId,
    Guid? PreviousConfigurationRevisionId,
    string ConfigurationDigest,
    string MarketAreaKey,
    IReadOnlyList<SubjectKindContract> SupportedListingKinds,
    long AggregateRevision,
    DateTimeOffset OccurredAtUtc);

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
    long ActivationRevision,
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

/// <summary>
/// Minimal public safety event. Catalog private evidence and transition notes remain in Catalog.
/// </summary>
public sealed record CatalogPublicVisibilitySuppressionChanged(
    Guid EventId,
    Guid SuppressionId,
    string CatalogKey,
    PublicVisibilitySuppressionTargetContract Target,
    string PublicReasonClass,
    PublicVisibilitySuppressionResponseModeContract ResponseMode,
    PublicVisibilitySuppressionStateContract State,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    long AggregateRevision,
    DateTimeOffset OccurredAtUtc);
