namespace Aggregator.Analytics.Application;

/// <summary>Requests deterministic materialization for a closed UTC date range.</summary>
public sealed record RebuildDailyAnalyticsMetricsRequest(
    DateOnly FromInclusive,
    DateOnly ToExclusive);

/// <summary>Reports the exact bounded effect of one Analytics aggregate rebuild.</summary>
public sealed record AnalyticsAggregateRebuildResult(
    DateOnly FromInclusive,
    DateOnly ToExclusive,
    int MaterializedMetricCount,
    int RemovedStaleMetricCount,
    DateTimeOffset CompletedAtUtc);

/// <summary>Persists complete daily aggregates from accepted events and exact public-read memberships.</summary>
public interface IAnalyticsAggregateWriter
{
    public Task<AnalyticsAggregateRebuildResult> RebuildAsync(
        RebuildDailyAnalyticsMetricsRequest request,
        DateTimeOffset calculatedAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Owns validation and orchestration of closed-range daily Analytics materialization.</summary>
public sealed class RebuildDailyAnalyticsMetricsService(
    IAnalyticsAggregateWriter aggregateWriter,
    TimeProvider timeProvider)
{
    public Task<AnalyticsAggregateRebuildResult> RebuildAsync(
        RebuildDailyAnalyticsMetricsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var todayUtc = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (request.ToExclusive <= request.FromInclusive)
        {
            throw InvalidRange(
                "ANALYTICS_AGGREGATION_RANGE_EMPTY",
                "Analytics aggregate range must be non-empty and use [from, to) semantics.");
        }

        if (request.ToExclusive > todayUtc)
        {
            throw InvalidRange(
                "ANALYTICS_AGGREGATION_RANGE_OPEN",
                "Analytics aggregate range cannot include the current or a future UTC day.");
        }

        if (request.ToExclusive.DayNumber - request.FromInclusive.DayNumber > 31)
        {
            throw InvalidRange(
                "ANALYTICS_AGGREGATION_RANGE_TOO_LARGE",
                "One Analytics aggregate rebuild cannot exceed 31 UTC days.");
        }

        return aggregateWriter.RebuildAsync(
            request,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private static AnalyticsCommandException InvalidRange(string code, string message) =>
        new(
            "Analytics.Aggregation",
            code,
            400,
            message,
            "Submit a bounded closed UTC date range and preserve [from, to) semantics.");
}
