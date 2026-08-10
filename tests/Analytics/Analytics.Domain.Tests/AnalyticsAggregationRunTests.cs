using Aggregator.Analytics.Domain;

namespace Analytics.Domain.Tests;

public sealed class AnalyticsAggregationRunTests
{
    private static readonly DateOnly FromInclusive = new(2026, 8, 1);
    private static readonly DateOnly ToExclusive = new(2026, 8, 3);
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 4, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RebuildingRunCannotContainTerminalResult()
    {
        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            AnalyticsAggregateRun.Restore(
                Guid.Parse("01990300-0000-7000-8000-000000000001"),
                FromInclusive,
                ToExclusive,
                AnalyticsAggregateRunState.Rebuilding,
                Timestamp,
                completedAtUtc: null,
                sourceDigest: new string('a', 64),
                materializedMetricCount: null,
                removedStaleMetricCount: null,
                materializedDayCount: null,
                failureCode: null,
                failureDetail: null,
                requiredAction: null));

        Assert.Equal("ANALYTICS_AGGREGATE_RUN_REBUILDING_SHAPE_INVALID", exception.Code);
    }

    [Fact]
    public void CompleteRunRequiresEveryTerminalResultField()
    {
        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            AnalyticsAggregateRun.Restore(
                Guid.Parse("01990300-0000-7000-8000-000000000002"),
                FromInclusive,
                ToExclusive,
                AnalyticsAggregateRunState.Complete,
                Timestamp,
                Timestamp.AddMinutes(1),
                new string('b', 64),
                materializedMetricCount: 2,
                removedStaleMetricCount: 0,
                materializedDayCount: null,
                failureCode: null,
                failureDetail: null,
                requiredAction: null));

        Assert.Equal("ANALYTICS_AGGREGATE_RUN_COMPLETE_SHAPE_INVALID", exception.Code);
    }

    [Fact]
    public void BlockedRunRequiresActionableOwnerFailure()
    {
        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            AnalyticsAggregateRun.Restore(
                Guid.Parse("01990300-0000-7000-8000-000000000003"),
                FromInclusive,
                ToExclusive,
                AnalyticsAggregateRunState.Blocked,
                Timestamp,
                Timestamp.AddMinutes(1),
                sourceDigest: null,
                materializedMetricCount: null,
                removedStaleMetricCount: null,
                materializedDayCount: null,
                failureCode: "ANALYTICS_SOURCE_BLOCKED",
                failureDetail: "Source projection is blocked.",
                requiredAction: null));

        Assert.Equal("ANALYTICS_AGGREGATE_RUN_BLOCKED_SHAPE_INVALID", exception.Code);
    }

    [Fact]
    public void CompletedDayRejectsNegativeMetricCount()
    {
        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            AnalyticsAggregateDayReadiness.Create(
                FromInclusive,
                Guid.Parse("01990300-0000-7000-8000-000000000004"),
                new string('c', 64),
                metricCount: -1,
                Timestamp));

        Assert.Equal("ANALYTICS_AGGREGATE_DAY_METRIC_COUNT_INVALID", exception.Code);
    }
}
