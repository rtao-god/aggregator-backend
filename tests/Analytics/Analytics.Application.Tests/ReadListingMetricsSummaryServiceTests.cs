using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;

namespace Analytics.Application.Tests;

public sealed class ReadListingMetricsSummaryServiceTests
{
    private static readonly Guid ActorId =
        Guid.Parse("01990400-0000-7000-8000-000000000001");
    private static readonly Guid ListingId =
        Guid.Parse("01990400-0000-7000-8000-000000000002");
    private static readonly DateOnly FromInclusive = new(2026, 8, 1);
    private static readonly DateOnly ToExclusive = new(2026, 8, 3);

    [Fact]
    public async Task CompleteDaysProduceDeterministicObservedSummary()
    {
        var metrics = new DailyListingMetrics[]
        {
            Complete(
                FromInclusive,
                new string('a', 64),
                InteractionCounts.Create(1, 2, 3, 4, 5, 6, 7, 8, 9)),
            Complete(
                FromInclusive.AddDays(1),
                new string('b', 64),
                InteractionCounts.Create(10, 20, 30, 40, 50, 60, 70, 80, 90)),
        };
        var service = CreateService(metrics);

        var first = await service.ReadAsync(
            ActorId,
            "berlin-recording-services",
            ListingId,
            new DailyMetricsRangeRequest(FromInclusive, ToExclusive),
            CancellationToken.None);
        var replay = await service.ReadAsync(
            ActorId,
            "berlin-recording-services",
            ListingId,
            new DailyMetricsRangeRequest(FromInclusive, ToExclusive),
            CancellationToken.None);

        Assert.Equal(AggregateReadinessStateContract.Complete, first.Readiness);
        Assert.Equal(2, first.SourceDayCount);
        Assert.Matches("^[0-9a-f]{64}$", first.AggregationSourceDigest);
        Assert.Equal(first.AggregationSourceDigest, replay.AggregationSourceDigest);
        Assert.Empty(first.UnavailableDays);
        Assert.NotNull(first.Counts);
        Assert.Equal(11, first.Counts.OrganicImpressions);
        Assert.Equal(22, first.Counts.SponsoredImpressions);
        Assert.Equal(33, first.Counts.ListingOpens);
        Assert.Equal(99, first.Counts.ExternalProfileClicks);
    }

    [Fact]
    public async Task PartialDayPreventsNumericSummary()
    {
        var service = CreateService(
        [
            Complete(
                FromInclusive,
                new string('c', 64),
                InteractionCounts.Create(1, 0, 0, 0, 0, 0, 0, 0, 0)),
            DailyListingMetrics.Unavailable(
                FromInclusive.AddDays(1),
                "berlin-recording-services",
                ListingId,
                new string('d', 64),
                sourceReadRevisionCount: 2,
                AggregateReadinessState.Partial,
                "late-events"),
        ]);

        var result = await service.ReadAsync(
            ActorId,
            "berlin-recording-services",
            ListingId,
            new DailyMetricsRangeRequest(FromInclusive, ToExclusive),
            CancellationToken.None);

        Assert.Equal(AggregateReadinessStateContract.Partial, result.Readiness);
        Assert.Null(result.AggregationSourceDigest);
        Assert.Null(result.Counts);
        var unavailable = Assert.Single(result.UnavailableDays);
        Assert.Equal(FromInclusive.AddDays(1), unavailable.Date);
        Assert.Equal("late-events", unavailable.Reason);
    }

    [Fact]
    public async Task BlockedDayHasPrecedenceOverPartialDay()
    {
        var service = CreateService(
        [
            DailyListingMetrics.Unavailable(
                FromInclusive,
                "berlin-recording-services",
                ListingId,
                new string('e', 64),
                sourceReadRevisionCount: 1,
                AggregateReadinessState.Partial,
                "late-events"),
            DailyListingMetrics.Unavailable(
                FromInclusive.AddDays(1),
                "berlin-recording-services",
                ListingId,
                new string('f', 64),
                sourceReadRevisionCount: 2,
                AggregateReadinessState.Blocked,
                "source-projection-blocked"),
        ]);

        var result = await service.ReadAsync(
            ActorId,
            "berlin-recording-services",
            ListingId,
            new DailyMetricsRangeRequest(FromInclusive, ToExclusive),
            CancellationToken.None);

        Assert.Equal(AggregateReadinessStateContract.Blocked, result.Readiness);
        Assert.Null(result.Counts);
        Assert.Equal(2, result.UnavailableDays.Count);
    }

    private static ReadListingMetricsSummaryService CreateService(
        IReadOnlyList<DailyListingMetrics> metrics) =>
        new(new ReadListingMetricsRangeService(
            new FixedMetricsStore(metrics),
            new AllowingMetricsAuthorizer()));

    private static DailyListingMetrics Complete(
        DateOnly date,
        string sourceDigest,
        InteractionCounts counts) =>
        DailyListingMetrics.Complete(
            date,
            "berlin-recording-services",
            ListingId,
            sourceDigest,
            sourceReadRevisionCount: 1,
            counts);

    private sealed class FixedMetricsStore(IReadOnlyList<DailyListingMetrics> metrics) :
        IDailyListingMetricsStore
    {
        public Task<IReadOnlyList<DailyListingMetrics>> GetRangeAsync(
            string catalogKey,
            Guid listingId,
            DateOnly fromInclusive,
            DateOnly toExclusive,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(metrics);
        }
    }

    private sealed class AllowingMetricsAuthorizer : IListingMetricsAuthorizer
    {
        public Task AuthorizeAsync(
            Guid actorId,
            Guid listingId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
