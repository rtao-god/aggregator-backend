using System.ComponentModel.DataAnnotations;

namespace Aggregator.Promotion.Contracts;

public enum PromotionCampaignStateContract
{
    Draft = 1,
    Active = 2,
    Suspended = 3,
    Completed = 4,
    Cancelled = 5,
}

public sealed record CreatePromotionCampaignRequest(
    Guid ProductRevisionId,
    Guid EntitlementId,
    Guid ListingId,
    [property: Required, RegularExpression("^[a-z][a-z0-9-]{0,95}$")]
    string CatalogKey,
    [property: Required, RegularExpression("^[a-z][a-z0-9-]{0,95}$")]
    string PlacementKey,
    [property: Range(1, 100)]
    int CapacityUnits,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc);

public sealed record PromotionCampaignRevisionRequest(long ExpectedAggregateRevision);

public sealed record SuspendPromotionCampaignRequest(
    long ExpectedAggregateRevision,
    [property: Required, MaxLength(300)]
    string Reason);

public sealed record PromotionCampaignResponse(
    Guid Id,
    Guid ProductRevisionId,
    Guid EntitlementId,
    Guid ListingId,
    string CatalogKey,
    string PlacementKey,
    int CapacityUnits,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    PromotionCampaignStateContract State,
    long AggregateRevision,
    DateTimeOffset LastChangedAtUtc,
    string? SuspensionReason,
    string Disclosure,
    bool Replayed);

public sealed record SponsoredPlacementItem(
    Guid CampaignId,
    Guid ListingId,
    string PlacementKey,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    string Disclosure);

public sealed record SponsoredPlacementResponse(
    string CatalogKey,
    string PlacementKey,
    DateTimeOffset EffectiveAtUtc,
    IReadOnlyList<SponsoredPlacementItem> Items);
