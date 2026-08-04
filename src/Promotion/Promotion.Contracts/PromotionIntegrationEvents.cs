namespace Aggregator.Promotion.Contracts;

public static class PromotionIntegrationEventTypes
{
    public const string EntitlementChanged = "promotion.entitlement.changed";
    public const string PlacementChanged = "promotion.placement.changed";
}

public static class PromotionIntegrationEventContracts
{
    public const string EntitlementChanged = "aggregator.promotion.entitlement-changed@1";
    public const string PlacementChanged = "aggregator.promotion.placement-changed@1";
}

public sealed record PromotionEntitlementChanged(
    Guid EventId,
    Guid EntitlementId,
    Guid ListingId,
    string ProductKey,
    PromotionEntitlementStateContract State,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    long AggregateRevision,
    DateTimeOffset OccurredAtUtc);

/// <summary>Minimal producer-owned event used to rebuild the Query sponsored-placement overlay.</summary>
public sealed record SponsoredPlacementChanged(
    Guid EventId,
    Guid PlacementId,
    Guid EntitlementId,
    Guid ListingId,
    string CatalogKey,
    string ProductKey,
    PlacementScopeTypeContract ScopeType,
    string ScopeKey,
    IReadOnlyList<string> LocaleScope,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    DateTimeOffset HardExpiryAtUtc,
    int PriorityBand,
    int CapacitySlot,
    string PresentationLabelKey,
    SponsoredPlacementStateContract State,
    long AggregateRevision,
    DateTimeOffset OccurredAtUtc);
