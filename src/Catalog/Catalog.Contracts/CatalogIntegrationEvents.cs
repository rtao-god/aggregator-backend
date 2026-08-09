namespace Aggregator.Catalog.Contracts;

public static class CatalogIntegrationEventTypes
{
    public const string ConfigurationActivated = "catalog.configuration.activated";
    public const string PublicationActivated = "catalog.publication.activated";
    public const string ListingClaimVerified = "catalog.listing-claim.verified";
    public const string ListingClaimRevoked = "catalog.listing-claim.revoked";
    public const string ListingAccessGrantChanged = "catalog.listing-access-grant.changed";
    public const string ListingPromotionEligibilityChanged =
        "catalog.listing-promotion-eligibility.changed";
    public const string PublicVisibilitySuppressionChanged =
        "catalog.public-visibility-suppression.changed";
}

public static class CatalogIntegrationEventContracts
{
    public const string ConfigurationActivated = "aggregator.catalog.configuration-activated@1";
    public const string PublicationActivated = "aggregator.catalog.publication-activated@2";
    public const string ListingClaimVerified = "aggregator.catalog.listing-claim-verified@1";
    public const string ListingClaimRevoked = "aggregator.catalog.listing-claim-revoked@1";
    public const string ListingAccessGrantChanged =
        "aggregator.catalog.listing-access-grant-changed@1";
    public const string ListingPromotionEligibilityChanged =
        "aggregator.catalog.listing-promotion-eligibility-changed@1";
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

public enum CatalogListingAccessGrantStateContract
{
    Active = 1,
    Revoked = 2,
}

/// <summary>
/// Minimal Catalog-owned resource permission change projected by Analytics and Promotion.
/// Claim evidence, JWT data, email, and reviewer notes never cross this boundary.
/// </summary>
public sealed record CatalogListingAccessGrantChanged(
    Guid EventId,
    Guid GrantId,
    Guid ListingId,
    Guid ActorId,
    IReadOnlyList<ListingAccessScopeContract> Permissions,
    CatalogListingAccessGrantStateContract State,
    DateTimeOffset GrantedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    long AggregateRevision,
    DateTimeOffset OccurredAtUtc);

/// <summary>Stable verified contact capability keys published by the Catalog owner.</summary>
public static class CatalogPromotionContactCapabilities
{
    public const string Website = "website";
    public const string Email = "email";
    public const string Phone = "phone";
    public const string WhatsApp = "whatsapp";
    public const string BookingReference = "booking-reference";
    public const string MapReference = "map-reference";
}

/// <summary>
/// Minimal Catalog-owned facts used by Promotion to evaluate one listing without reading Catalog storage.
/// </summary>
public sealed record CatalogListingPromotionEligibilityChanged(
    Guid EventId,
    string CatalogKey,
    Guid ListingId,
    Guid? PublishedListingRevisionId,
    bool IsPublished,
    bool IsArchived,
    bool HasBlockingDispute,
    bool HasVerifiedContact,
    IReadOnlyList<string> VerifiedContactCapabilities,
    IReadOnlyList<string> CategoryKeys,
    string? DistrictKey,
    long EligibilityRevision,
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
