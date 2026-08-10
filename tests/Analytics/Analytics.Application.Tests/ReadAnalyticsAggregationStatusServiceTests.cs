using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;

namespace Analytics.Application.Tests;

public sealed class ReadAnalyticsAggregationStatusServiceTests
{
    private static readonly DateOnly FromInclusive = new(2026, 8, 1);
    private static readonly DateOnly ToExclusive = new(2026, 8, 3);
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 4, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactCompletedDayEvidenceProducesCompleteStatus()
    {
        var store = new StubStore(new AnalyticsAggregationStatusEvidence(
        [
            CreateDay(FromInclusive, 1),
            CreateDay(FromInclusive.AddDays(1), 2),
        ],
        LatestRun: null));
        var service = new ReadAnalyticsAggregationStatusService(store);

        var result = await service.ReadAsync(
            new DailyMetricsRangeRequest(FromInclusive, ToExclusive),
            CancellationToken.None);

        Assert.Equal(AggregateReadinessStateContract.Complete, result.Readiness);
        Assert.Empty(result.MissingDates);
        Assert.Null(result.LatestRun);
        Assert.Null(result.UnavailableReason);
    }

    [Fact]
    public async Task ActiveRunProducesRebuildingInsteadOfObservedZero()
    {
        var run = AnalyticsAggregateRun.Restore(
            Guid.Parse("01990200-0000-7000-8000-000000000010"),
            FromInclusive,
            ToExclusive,
            AnalyticsAggregateRunState.Rebuilding,
            Timestamp,
            completedAtUtc: null,
            sourceDigest: null,
            materializedMetricCount: null,
            removedStaleMetricCount: null,
            materializedDayCount: null,
            failureCode: null,
            failureDetail: null,
            requiredAction: null);
        var service = new ReadAnalyticsAggregationStatusService(
            new StubStore(new AnalyticsAggregationStatusEvidence(
            [
                CreateDay(FromInclusive, 1),
                CreateDay(FromInclusive.AddDays(1), 2),
            ],
            run)));

        var result = await service.ReadAsync(
            new DailyMetricsRangeRequest(FromInclusive, ToExclusive),
            CancellationToken.None);

        Assert.Equal(AggregateReadinessStateContract.Rebuilding, result.Readiness);
        Assert.Empty(result.MissingDates);
        Assert.Equal(AnalyticsAggregateRunStateContract.Rebuilding, result.LatestRun?.State);
        Assert.Equal("aggregation-rebuilding", result.UnavailableReason);
    }

    [Fact]
    public async Task BlockedRunPreservesOwnerFailureCode()
    {
        var run = AnalyticsAggregateRun.Restore(
            Guid.Parse("01990200-0000-7000-8000-000000000020"),
            FromInclusive,
            ToExclusive,
            AnalyticsAggregateRunState.Blocked,
            Timestamp,
            Timestamp.AddMinutes(1),
            sourceDigest: null,
            materializedMetricCount: null,
            removedStaleMetricCount: null,
            materializedDayCount: null,
            failureCode: "ANALYTICS_SOURCE_PROJECTION_BLOCKED",
            failureDetail: "Public-reference projection is not complete.",
            requiredAction: "Replay the exact public-read activation stream.");
        var service = new ReadAnalyticsAggregationStatusService(
            new StubStore(new AnalyticsAggregationStatusEvidence([], run)));

        var result = await service.ReadAsync(
            new DailyMetricsRangeRequest(FromInclusive, ToExclusive),
            CancellationToken.None);

        Assert.Equal(AggregateReadinessStateContract.Blocked, result.Readiness);
        Assert.Equal(
            "ANALYTICS_SOURCE_PROJECTION_BLOCKED",
            result.UnavailableReason);
        Assert.Equal(
            "Replay the exact public-read activation stream.",
            result.LatestRun?.RequiredAction);
    }

    [Fact]
    public async Task MissingEvidenceWithoutRelevantRunRemainsPartial()
    {
        var service = new ReadAnalyticsAggregationStatusService(
            new StubStore(new AnalyticsAggregationStatusEvidence(
                [CreateDay(FromInclusive, 1)],
                LatestRun: null)));

        var result = await service.ReadAsync(
            new DailyMetricsRangeRequest(FromInclusive, ToExclusive),
            CancellationToken.None);

        Assert.Equal(AggregateReadinessStateContract.Partial, result.Readiness);
        Assert.Equal([FromInclusive.AddDays(1)], result.MissingDates);
        Assert.Equal("aggregation-not-materialized", result.UnavailableReason);
    }

    [Fact]
    public async Task CompleteRunWithoutAllDayReadinessIsPersistenceCorruption()
    {
        var run = AnalyticsAggregateRun.Restore(
            Guid.Parse("01990200-0000-7000-8000-000000000030"),
            FromInclusive,
            ToExclusive,
            AnalyticsAggregateRunState.Complete,
            Timestamp,
            Timestamp.AddMinutes(1),
            new string('c', 64),
            materializedMetricCount: 2,
            removedStaleMetricCount: 0,
            materializedDayCount: 2,
            failureCode: null,
            failureDetail: null,
            requiredAction: null);
        var service = new ReadAnalyticsAggregationStatusService(
            new StubStore(new AnalyticsAggregationStatusEvidence(
                [CreateDay(FromInclusive, 1)],
                run)));

        var exception = await Assert.ThrowsAsync<AnalyticsCommandException>(() =>
            service.ReadAsync(
                new DailyMetricsRangeRequest(FromInclusive, ToExclusive),
                CancellationToken.None));

        Assert.Equal("ANALYTICS_AGGREGATION_STATUS_EVIDENCE_CORRUPT", exception.Code);
        Assert.Equal(500, exception.StatusCode);
    }

    [Fact]
    public async Task DuplicateCompletedDayIsPersistenceCorruption()
    {
        var duplicateDate = FromInclusive;
        var service = new ReadAnalyticsAggregationStatusService(
            new StubStore(new AnalyticsAggregationStatusEvidence(
            [
                CreateDay(duplicateDate, 1),
                CreateDay(duplicateDate, 2),
            ],
            LatestRun: null)));

        var exception = await Assert.ThrowsAsync<AnalyticsCommandException>(() =>
            service.ReadAsync(
                new DailyMetricsRangeRequest(FromInclusive, ToExclusive),
                CancellationToken.None));

        Assert.Equal("ANALYTICS_AGGREGATION_STATUS_EVIDENCE_CORRUPT", exception.Code);
        Assert.Equal(500, exception.StatusCode);
    }

    private static AnalyticsAggregateDayReadiness CreateDay(DateOnly date, int suffix) =>
        AnalyticsAggregateDayReadiness.Create(
            date,
            Guid.Parse($"01990200-0000-7000-8000-{suffix:000000000000}"),
            new string((char)('a' + suffix - 1), 64),
            metricCount: suffix,
            Timestamp.AddMinutes(suffix));

    private sealed class StubStore(AnalyticsAggregationStatusEvidence evidence) :
        IAnalyticsAggregationOperationStore
    {
        public Task<AnalyticsAggregationLease> BeginAsync(
            Guid runId,
            Guid leaseToken,
            RebuildDailyAnalyticsMetricsRequest request,
            DateTimeOffset startedAtUtc,
            DateTimeOffset leaseExpiresAtUtc,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Status test does not start aggregation work.");

        public Task MarkBlockedAsync(
            AnalyticsAggregationLease lease,
            AnalyticsAggregationFailure failure,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Status test does not mutate aggregation work.");

        public Task<AnalyticsAggregationStatusEvidence> ReadStatusEvidenceAsync(
            DateOnly fromInclusive,
            DateOnly toExclusive,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(FromInclusive, fromInclusive);
            Assert.Equal(ToExclusive, toExclusive);
            return Task.FromResult(evidence);
        }
    }
}
