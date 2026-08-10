using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;

namespace Analytics.Application.Tests;

public sealed class PromotionUsageWindowDerivationTests
{
    private static readonly DateTimeOffset StartsAtUtc =
        new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid PlacementId =
        Guid.Parse("0198ffb0-0000-7000-8000-000000000001");
    private static readonly Guid ListingId =
        Guid.Parse("0198ffb0-0000-7000-8000-000000000002");

    [Fact]
    public void AcceptedSponsoredEventsProduceOneDeterministicDailyWindow()
    {
        var events = new[]
        {
            Create(
                "0198ffb0-0000-7000-8000-000000000005",
                InteractionEventKind.WebsiteClicked,
                StartsAtUtc.AddMinutes(3),
                'c'),
            Create(
                "0198ffb0-0000-7000-8000-000000000003",
                InteractionEventKind.ListingImpression,
                StartsAtUtc.AddMinutes(1),
                'a'),
            Create(
                "0198ffb0-0000-7000-8000-000000000004",
                InteractionEventKind.ListingOpened,
                StartsAtUtc.AddMinutes(2),
                'b'),
        };

        var result = PromotionUsageWindowDeriver.Derive(
            events,
            new DateOnly(2026, 8, 9),
            new DateOnly(2026, 8, 10));

        var window = Assert.Single(result);
        Assert.Equal(PlacementId, window.PlacementId);
        Assert.Equal(ListingId, window.ListingId);
        Assert.Equal("berlin-recording-services", window.CatalogKey);
        Assert.Equal(StartsAtUtc, window.WindowStartsAtUtc);
        Assert.Equal(StartsAtUtc.AddDays(1), window.WindowEndsAtUtc);
        Assert.Equal(1, window.AcceptedImpressions);
        Assert.Equal(1, window.AcceptedListingOpens);
        Assert.Equal(1, window.AcceptedOutboundClicks);
        Assert.Equal(
            "292aecaa28ca51f01777d587be95d1b772251048b5f37286c5c3f964e70b1002",
            window.SourceDigest);
    }

    [Fact]
    public void PlacementIdentityDivergenceIsRejected()
    {
        var events = new[]
        {
            Create(
                "0198ffb0-0000-7000-8000-000000000006",
                InteractionEventKind.ListingImpression,
                StartsAtUtc.AddMinutes(1),
                'd'),
            Create(
                "0198ffb0-0000-7000-8000-000000000007",
                InteractionEventKind.ListingOpened,
                StartsAtUtc.AddMinutes(2),
                'e') with
            {
                ListingId = Guid.Parse("0198ffb0-0000-7000-8000-000000000099"),
            },
        };

        var exception = Assert.Throws<AnalyticsCommandException>(() =>
            PromotionUsageWindowDeriver.Derive(
                events,
                new DateOnly(2026, 8, 9),
                new DateOnly(2026, 8, 10)));

        Assert.Equal("ANALYTICS_PROMOTION_USAGE_IDENTITY_DIVERGED", exception.Code);
    }

    [Fact]
    public void NonUsageSponsoredEventsDoNotCreateFakeZeroWindow()
    {
        var result = PromotionUsageWindowDeriver.Derive(
            [Create(
                "0198ffb0-0000-7000-8000-000000000008",
                InteractionEventKind.ClaimStarted,
                StartsAtUtc.AddMinutes(1),
                'f')],
            new DateOnly(2026, 8, 9),
            new DateOnly(2026, 8, 10));

        Assert.Empty(result);
    }

    [Fact]
    public void EventOutsideExactRangeIsRejected()
    {
        var exception = Assert.Throws<AnalyticsCommandException>(() =>
            PromotionUsageWindowDeriver.Derive(
                [Create(
                    "0198ffb0-0000-7000-8000-000000000009",
                    InteractionEventKind.ListingImpression,
                    StartsAtUtc.AddDays(1),
                    '1')],
                new DateOnly(2026, 8, 9),
                new DateOnly(2026, 8, 10)));

        Assert.Equal("ANALYTICS_PROMOTION_USAGE_EVENT_OUTSIDE_RANGE", exception.Code);
    }

    private static AcceptedSponsoredInteraction Create(
        string eventId,
        InteractionEventKind kind,
        DateTimeOffset occurredAtUtc,
        char digestCharacter) =>
        new(
            Guid.Parse(eventId),
            kind,
            "berlin-recording-services",
            ListingId,
            PlacementId,
            occurredAtUtc,
            new string(digestCharacter, 64));
}
