namespace Aggregator.Analytics.Infrastructure;

/// <summary>
/// Exact retained interaction fields permitted to participate in aggregate and Promotion usage materialization.
/// </summary>
internal sealed record AnalyticsAggregateInteractionProjection(
    Guid Id,
    int EventKind,
    string CatalogKey,
    Guid? ListingId,
    Guid PublicReadRevisionId,
    DateTimeOffset OccurredAtUtc,
    int PlacementExposureKind,
    Guid? PlacementId,
    int QualityState,
    string PayloadDigest);
