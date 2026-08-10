using Aggregator.Promotion.Application;

namespace Promotion.Application.Tests;

public sealed class AnalyticsPromotionUsageProjectionTests
{
    private static readonly DateTimeOffset StartsAtUtc =
        new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndsAtUtc = StartsAtUtc.AddDays(1);
    private static readonly Guid MessageId =
        Guid.Parse("0198ff80-0000-7000-8000-000000000001");

    [Fact]
    public async Task ClosedAnalyticsWindowCreatesExactLocalProjection()
    {
        var store = new CapturingStore();
        var service = new ApplyAnalyticsPromotionUsageWindowService(
            store,
            new FixedClock(EndsAtUtc.AddMinutes(10)));

        var result = await service.ApplyAsync(CreateMessage(), CancellationToken.None);

        Assert.Equal(PromotionUsageProjectionDisposition.Applied, result.Disposition);
        var change = Assert.IsType<PromotionUsageProjectionChange>(store.Change);
        Assert.Equal(MessageId, change.MessageId);
        Assert.Equal(CreateMessage().UsageWindowId, change.Projection.UsageWindowId);
        Assert.Equal(CreateMessage().PlacementId, change.Projection.PlacementId);
        Assert.Equal(CreateMessage().ListingId, change.Projection.ListingId);
        Assert.Equal("berlin-recording-services", change.Projection.CatalogKey);
        Assert.Equal(15, change.Projection.AcceptedImpressions);
        Assert.Equal(6, change.Projection.AcceptedListingOpens);
        Assert.Equal(3, change.Projection.AcceptedOutboundClicks);
        Assert.Equal(4, change.Projection.SourceAggregateRevision);
        Assert.Equal(MessageId, change.Projection.SourceMessageId);
    }

    [Fact]
    public async Task BrokerAndProducerIdentityMismatchIsRejected()
    {
        var store = new CapturingStore();
        var service = new ApplyAnalyticsPromotionUsageWindowService(
            store,
            new FixedClock(EndsAtUtc.AddMinutes(10)));
        var invalid = CreateMessage() with
        {
            EventId = Guid.Parse("0198ff80-0000-7000-8000-000000000099"),
        };

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() =>
            service.ApplyAsync(invalid, CancellationToken.None));

        Assert.Equal("PROMOTION_USAGE_MESSAGE_ID_MISMATCH", exception.Code);
        Assert.Null(store.Change);
    }

    [Fact]
    public async Task NegativeCountIsRejectedBeforePersistence()
    {
        var store = new CapturingStore();
        var service = new ApplyAnalyticsPromotionUsageWindowService(
            store,
            new FixedClock(EndsAtUtc.AddMinutes(10)));
        var invalid = CreateMessage() with { AcceptedOutboundClicks = -1 };

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() =>
            service.ApplyAsync(invalid, CancellationToken.None));

        Assert.Equal("PROMOTION_USAGE_COUNT_INVALID", exception.Code);
        Assert.Null(store.Change);
    }

    [Fact]
    public async Task CompleteZeroCorrectionIsPersistedExplicitly()
    {
        var store = new CapturingStore();
        var service = new ApplyAnalyticsPromotionUsageWindowService(
            store,
            new FixedClock(EndsAtUtc.AddMinutes(10)));
        var correction = CreateMessage() with
        {
            AcceptedImpressions = 0,
            AcceptedListingOpens = 0,
            AcceptedOutboundClicks = 0,
            AggregateRevision = 5,
        };

        await service.ApplyAsync(correction, CancellationToken.None);

        var projection = Assert.IsType<PromotionUsageProjectionChange>(store.Change).Projection;
        Assert.Equal(0, projection.AcceptedImpressions);
        Assert.Equal(0, projection.AcceptedListingOpens);
        Assert.Equal(0, projection.AcceptedOutboundClicks);
        Assert.Equal(5, projection.SourceAggregateRevision);
    }

    [Fact]
    public async Task FutureWindowIsRejectedBeforePersistence()
    {
        var store = new CapturingStore();
        var service = new ApplyAnalyticsPromotionUsageWindowService(
            store,
            new FixedClock(EndsAtUtc.AddMinutes(10)));
        var invalid = CreateMessage() with { OccurredAtUtc = EndsAtUtc.AddTicks(-1) };

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() =>
            service.ApplyAsync(invalid, CancellationToken.None));

        Assert.Equal("PROMOTION_USAGE_WINDOW_INVALID", exception.Code);
        Assert.Null(store.Change);
    }

    private static AnalyticsPromotionUsageProjectionMessage CreateMessage() =>
        new(
            MessageId,
            "analytics.promotion-usage-window-closed@1",
            new string('a', 64),
            "promotion-usage-test",
            causationId: null,
            MessageId,
            Guid.Parse("0198ff80-0000-7000-8000-000000000002"),
            Guid.Parse("0198ff80-0000-7000-8000-000000000003"),
            Guid.Parse("0198ff80-0000-7000-8000-000000000004"),
            "berlin-recording-services",
            StartsAtUtc,
            EndsAtUtc,
            AcceptedImpressions: 15,
            AcceptedListingOpens: 6,
            AcceptedOutboundClicks: 3,
            Guid.Parse("0198ff80-0000-7000-8000-000000000005"),
            AggregateRevision: 4,
            EndsAtUtc.AddMinutes(5));

    private sealed class FixedClock(DateTimeOffset nowUtc) : IPromotionClock
    {
        public DateTimeOffset GetUtcNow() => nowUtc;
    }

    private sealed class CapturingStore : IPromotionUsageProjectionStore
    {
        public PromotionUsageProjectionChange? Change { get; private set; }

        public Task<PromotionUsageProjectionResult> ApplyAsync(
            PromotionUsageProjectionChange change,
            DateTimeOffset receivedAtUtc,
            CancellationToken cancellationToken)
        {
            Change = change;
            return Task.FromResult(new PromotionUsageProjectionResult(
                change.Projection,
                PromotionUsageProjectionDisposition.Applied));
        }
    }
}
