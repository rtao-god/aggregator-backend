namespace Aggregator.Analytics.Contracts;

/// <summary>Producer-owned wire identity for Analytics-approved Promotion usage.</summary>
public static class AnalyticsPromotionUsageIntegrationContracts
{
    public const string RoutingKey = "analytics.promotion-usage-window.closed";

    public const string ContractIdentity = "analytics.promotion-usage-window-closed@1";

    public const int ContractRevision = 1;
}

/// <summary>
/// Analytics-owned, quality-filtered usage for one exact sponsored placement and one closed UTC window.
/// Promotion consumes these aggregates and must not re-run Analytics traffic-quality rules.
/// </summary>
public sealed record PromotionUsageWindowClosed(
    Guid EventId,
    Guid UsageWindowId,
    Guid PlacementId,
    Guid ListingId,
    string CatalogKey,
    DateTimeOffset WindowStartsAtUtc,
    DateTimeOffset WindowEndsAtUtc,
    long AcceptedImpressions,
    long AcceptedListingOpens,
    long AcceptedOutboundClicks,
    Guid AggregationRunId,
    long AggregateRevision,
    DateTimeOffset OccurredAtUtc);
