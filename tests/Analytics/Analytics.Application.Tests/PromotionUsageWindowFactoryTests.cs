using Aggregator.Analytics.Application;

namespace Analytics.Application.Tests;

public sealed class PromotionUsageWindowFactoryTests
{
    private static readonly DateTimeOffset StartsAtUtc =
        new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndsAtUtc = StartsAtUtc.AddDays(1);

    [Fact]
    public void ClosedNonEmptyWindowCreatesExactProducerEvent()
    {
        var window = CreateWindow();
        var eventId = Guid.Parse("0198ff70-0000-7000-8000-000000000006");
        var occurredAtUtc = EndsAtUtc.AddMinutes(5);

        var integrationEvent = PromotionUsageWindowFactory.Create(
            window,
            eventId,
            occurredAtUtc);

        Assert.Equal(eventId, integrationEvent.EventId);
        Assert.Equal(window.UsageWindowId, integrationEvent.UsageWindowId);
        Assert.Equal(window.PlacementId, integrationEvent.PlacementId);
        Assert.Equal(window.ListingId, integrationEvent.ListingId);
        Assert.Equal(window.CatalogKey, integrationEvent.CatalogKey);
        Assert.Equal(StartsAtUtc, integrationEvent.WindowStartsAtUtc);
        Assert.Equal(EndsAtUtc, integrationEvent.WindowEndsAtUtc);
        Assert.Equal(12, integrationEvent.AcceptedImpressions);
        Assert.Equal(4, integrationEvent.AcceptedListingOpens);
        Assert.Equal(2, integrationEvent.AcceptedOutboundClicks);
        Assert.Equal(window.AggregationRunId, integrationEvent.AggregationRunId);
        Assert.Equal(3, integrationEvent.AggregateRevision);
        Assert.Equal(occurredAtUtc, integrationEvent.OccurredAtUtc);
    }

    [Fact]
    public void WindowThatHasNotClosedIsRejected()
    {
        var exception = Assert.Throws<AnalyticsCommandException>(() =>
            PromotionUsageWindowFactory.Create(
                CreateWindow(),
                Guid.Parse("0198ff70-0000-7000-8000-000000000007"),
                EndsAtUtc.AddTicks(-1)));

        Assert.Equal("ANALYTICS_PROMOTION_USAGE_WINDOW_NOT_CLOSED", exception.Code);
    }

    [Fact]
    public void EmptyWindowIsNotPublished()
    {
        var empty = CreateWindow() with
        {
            AcceptedImpressions = 0,
            AcceptedListingOpens = 0,
            AcceptedOutboundClicks = 0,
        };

        var exception = Assert.Throws<AnalyticsCommandException>(() =>
            PromotionUsageWindowFactory.Create(
                empty,
                Guid.Parse("0198ff70-0000-7000-8000-000000000008"),
                EndsAtUtc));

        Assert.Equal("ANALYTICS_PROMOTION_USAGE_EMPTY", exception.Code);
    }

    [Fact]
    public void NegativeAcceptedCountIsRejected()
    {
        var invalid = CreateWindow() with { AcceptedOutboundClicks = -1 };

        var exception = Assert.Throws<AnalyticsCommandException>(() =>
            PromotionUsageWindowFactory.Create(
                invalid,
                Guid.Parse("0198ff70-0000-7000-8000-000000000009"),
                EndsAtUtc));

        Assert.Equal("ANALYTICS_PROMOTION_USAGE_COUNT_INVALID", exception.Code);
    }

    [Fact]
    public void NonUtcWindowIsRejected()
    {
        var invalid = CreateWindow() with
        {
            WindowStartsAtUtc = StartsAtUtc.ToOffset(TimeSpan.FromHours(4)),
        };

        var exception = Assert.Throws<AnalyticsCommandException>(() =>
            PromotionUsageWindowFactory.Create(
                invalid,
                Guid.Parse("0198ff70-0000-7000-8000-000000000010"),
                EndsAtUtc));

        Assert.Equal("ANALYTICS_PROMOTION_USAGE_TIME_NOT_UTC", exception.Code);
    }

    private static ClosedPromotionUsageWindow CreateWindow() =>
        new(
            Guid.Parse("0198ff70-0000-7000-8000-000000000001"),
            Guid.Parse("0198ff70-0000-7000-8000-000000000002"),
            Guid.Parse("0198ff70-0000-7000-8000-000000000003"),
            "berlin-recording-services",
            StartsAtUtc,
            EndsAtUtc,
            AcceptedImpressions: 12,
            AcceptedListingOpens: 4,
            AcceptedOutboundClicks: 2,
            Guid.Parse("0198ff70-0000-7000-8000-000000000004"),
            AggregateRevision: 3);
}
