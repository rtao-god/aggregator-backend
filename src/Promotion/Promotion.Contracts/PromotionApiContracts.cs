namespace Aggregator.Promotion.Contracts;

public static class PromotionContractIdentity
{
    public const string AdminApi = "aggregator-promotion-admin";
    public const int AdminApiRevision = 1;
}

public enum PromotionProductStateContract
{
    Active = 1,
    Inactive = 2,
    Archived = 3,
}

public enum PromotionPresentationFeatureContract
{
    FeaturedListing = 1,
    SponsoredSlot = 2,
    ExtendedCard = 3,
    ExtendedGallery = 4,
}

public enum PromotionEntitlementSourceTypeContract
{
    ManualContract = 1,
    ManualTrial = 2,
    AdministrativeGrant = 3,
}

public enum PromotionEntitlementStateContract
{
    Scheduled = 1,
    Active = 2,
    Paused = 3,
    Revoked = 4,
    Expired = 5,
}

public enum PlacementScopeTypeContract
{
    Catalog = 1,
    Category = 2,
    District = 3,
    EditorialLanding = 4,
}

public enum SponsoredPlacementStateContract
{
    Scheduled = 1,
    Active = 2,
    Paused = 3,
    Ended = 4,
    Revoked = 5,
}

public sealed record CreatePromotionProductRequest(
    string ContractIdentity,
    int ContractRevision,
    string Key,
    IReadOnlyDictionary<string, string> DisplayNames,
    IReadOnlyList<PromotionPresentationFeatureContract> PresentationFeatures,
    bool RequiresVerifiedContact,
    string? RequiredContactCapability);

public sealed record CreatePromotionProductRevisionRequest(
    long ExpectedAggregateRevision,
    IReadOnlyDictionary<string, string> DisplayNames,
    IReadOnlyList<PromotionPresentationFeatureContract> PresentationFeatures,
    bool RequiresVerifiedContact,
    string? RequiredContactCapability);

public sealed record ChangePromotionProductStateRequest(
    long ExpectedAggregateRevision,
    PromotionProductStateContract State);

public sealed record PromotionProductRevisionResponse(
    Guid Id,
    Guid ProductId,
    long RevisionNumber,
    IReadOnlyDictionary<string, string> DisplayNames,
    IReadOnlyList<PromotionPresentationFeatureContract> PresentationFeatures,
    bool RequiresVerifiedContact,
    string? RequiredContactCapability,
    Guid CreatedByActorId,
    DateTimeOffset CreatedAtUtc,
    string ContentDigest);

public sealed record PromotionProductResponse(
    Guid Id,
    string Key,
    PromotionProductStateContract State,
    PromotionProductRevisionResponse CurrentRevision,
    long AggregateRevision);

public sealed record GrantPromotionEntitlementRequest(
    Guid ListingId,
    string ProductKey,
    PromotionEntitlementSourceTypeContract SourceType,
    string ExternalReference,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    string AuditReason);

public sealed record ChangePromotionEntitlementStateRequest(
    long ExpectedAggregateRevision,
    string AuditReason);

public sealed record PromotionEntitlementResponse(
    Guid Id,
    Guid ListingId,
    string ProductKey,
    PromotionEntitlementSourceTypeContract SourceType,
    string ExternalReference,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    PromotionEntitlementStateContract State,
    Guid CreatedByActorId,
    string AuditReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ChangedAtUtc,
    long AggregateRevision);

public sealed record CreateSponsoredPlacementRequest(
    Guid EntitlementId,
    string CatalogKey,
    PlacementScopeTypeContract ScopeType,
    string ScopeKey,
    IReadOnlyList<string> LocaleScope,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    int PriorityBand,
    int CapacitySlot,
    string PresentationLabelKey,
    string AuditReason);

public sealed record CreateSponsoredPlacementRevisionRequest(
    long ExpectedAggregateRevision,
    PlacementScopeTypeContract ScopeType,
    string ScopeKey,
    IReadOnlyList<string> LocaleScope,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    int PriorityBand,
    int CapacitySlot,
    string PresentationLabelKey,
    string AuditReason);

public sealed record ChangeSponsoredPlacementStateRequest(
    long ExpectedAggregateRevision,
    string AuditReason);

public sealed record SponsoredPlacementRevisionResponse(
    Guid Id,
    Guid PlacementId,
    long RevisionNumber,
    string CatalogKey,
    PlacementScopeTypeContract ScopeType,
    string ScopeKey,
    IReadOnlyList<string> LocaleScope,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    int PriorityBand,
    int CapacitySlot,
    string PresentationLabelKey,
    Guid CreatedByActorId,
    DateTimeOffset CreatedAtUtc,
    string ContentDigest);

public sealed record SponsoredPlacementResponse(
    Guid Id,
    Guid EntitlementId,
    Guid ListingId,
    string ProductKey,
    SponsoredPlacementStateContract State,
    SponsoredPlacementRevisionResponse CurrentRevision,
    DateTimeOffset HardExpiryAtUtc,
    DateTimeOffset ChangedAtUtc,
    string AuditReason,
    long AggregateRevision);

public sealed record PromotionPlacementCalendarResponse(
    string CatalogKey,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<SponsoredPlacementResponse> Placements);
