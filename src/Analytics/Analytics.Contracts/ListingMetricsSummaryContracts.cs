namespace Aggregator.Analytics.Contracts;

/// <summary>Preserves one unavailable day that prevents a complete listing summary.</summary>
public sealed record ListingMetricsSummaryUnavailableDay(
    DateOnly Date,
    AggregateReadinessStateContract Readiness,
    string Reason);

/// <summary>Returns a complete listing summary or an explicit non-numeric readiness state.</summary>
public sealed record ListingMetricsSummaryResponse(
    string CatalogKey,
    Guid ListingId,
    DateOnly FromInclusive,
    DateOnly ToExclusive,
    AggregateReadinessStateContract Readiness,
    string? AggregationSourceDigest,
    int SourceDayCount,
    InteractionCountsContract? Counts,
    IReadOnlyList<ListingMetricsSummaryUnavailableDay> UnavailableDays);
