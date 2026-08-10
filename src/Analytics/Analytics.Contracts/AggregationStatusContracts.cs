namespace Aggregator.Analytics.Contracts;

/// <summary>Declares the persisted lifecycle of one Analytics aggregation run.</summary>
public enum AnalyticsAggregateRunStateContract
{
    Rebuilding = 1,
    Complete = 2,
    Blocked = 3,
}

/// <summary>Exposes exact evidence for the latest run relevant to an aggregation-status request.</summary>
public sealed record AnalyticsAggregateRunResponse(
    Guid RunId,
    DateOnly FromInclusive,
    DateOnly ToExclusive,
    AnalyticsAggregateRunStateContract State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? SourceDigest,
    int? MaterializedMetricCount,
    int? RemovedStaleMetricCount,
    int? MaterializedDayCount,
    string? FailureCode,
    string? FailureDetail,
    string? RequiredAction);

/// <summary>Reports complete, partial, blocked, or rebuilding evidence for one exact UTC date range.</summary>
public sealed record AnalyticsAggregationStatusResponse(
    DateOnly FromInclusive,
    DateOnly ToExclusive,
    AggregateReadinessStateContract Readiness,
    IReadOnlyList<DateOnly> MissingDates,
    AnalyticsAggregateRunResponse? LatestRun,
    string? UnavailableReason);
