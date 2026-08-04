using Aggregator.Analytics.Domain;

namespace Analytics.Domain.Tests;

public sealed class AnalyticsDomainInvariantTests
{
    private static readonly DateTimeOffset ReceivedAtUtc =
        new(2026, 8, 4, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ListingInteractionRequiresListingIdentity()
    {
        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            CreateEvent(
                InteractionEventKind.ListingOpened,
                listingId: null,
                PlacementContext.Create(
                    PlacementExposureKind.Organic,
                    placementId: null,
                    scopeKey: "catalog")));

        Assert.Equal("ANALYTICS_LISTING_REQUIRED", exception.Code);
    }

    [Fact]
    public void SearchResultsInteractionForbidsListingIdentity()
    {
        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            CreateEvent(
                InteractionEventKind.SearchResultsViewed,
                Guid.Parse("0198a100-0000-7000-8000-000000000001"),
                PlacementContext.Create(
                    PlacementExposureKind.NotApplicable,
                    placementId: null,
                    scopeKey: null)));

        Assert.Equal("ANALYTICS_LISTING_FORBIDDEN", exception.Code);
    }

    [Fact]
    public void SponsoredPlacementRequiresExactPlacementIdentity()
    {
        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            PlacementContext.Create(
                PlacementExposureKind.Sponsored,
                placementId: null,
                scopeKey: "recording-studio"));

        Assert.Equal("ANALYTICS_SPONSORED_PLACEMENT_REQUIRED", exception.Code);
    }

    [Fact]
    public void CampaignParametersRejectUnknownKeys()
    {
        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            InteractionEvent.CreateAccepted(
                Guid.Parse("0198a100-0000-7000-8000-000000000002"),
                Guid.Parse("0198a100-0000-7000-8000-000000000003"),
                InteractionEventKind.ListingImpression,
                "berlin-recording-services",
                Guid.Parse("0198a100-0000-7000-8000-000000000004"),
                Guid.Parse("0198a100-0000-7000-8000-000000000005"),
                ReceivedAtUtc,
                ReceivedAtUtc,
                "catalog_results",
                PlacementContext.Create(
                    PlacementExposureKind.Organic,
                    placementId: null,
                    scopeKey: "recording-studio"),
                ReferrerClass.Campaign,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["free_form"] = "personal-data",
                },
                ConsentMode.AnalyticsAllowed,
                new string('a', 64)));

        Assert.Equal("ANALYTICS_CAMPAIGN_PARAMETER_FORBIDDEN", exception.Code);
    }

    [Fact]
    public void EventTimeOutsideOwnerBoundsIsRejected()
    {
        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            InteractionEvent.CreateAccepted(
                Guid.Parse("0198a100-0000-7000-8000-000000000006"),
                Guid.Parse("0198a100-0000-7000-8000-000000000007"),
                InteractionEventKind.ListingOpened,
                "berlin-recording-services",
                Guid.Parse("0198a100-0000-7000-8000-000000000008"),
                Guid.Parse("0198a100-0000-7000-8000-000000000009"),
                ReceivedAtUtc.AddDays(-8),
                ReceivedAtUtc,
                "listing_card",
                PlacementContext.Create(
                    PlacementExposureKind.Organic,
                    placementId: null,
                    scopeKey: "recording-studio"),
                ReferrerClass.Internal,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ConsentMode.EssentialOnly,
                new string('b', 64)));

        Assert.Equal("ANALYTICS_EVENT_TIME_OUT_OF_BOUNDS", exception.Code);
    }

    [Fact]
    public void UnavailableAggregateCannotMasqueradeAsObservedZero()
    {
        var metrics = DailyListingMetrics.Unavailable(
            new DateOnly(2026, 8, 4),
            "berlin-recording-services",
            Guid.Parse("0198a100-0000-7000-8000-000000000010"),
            new string('c', 64),
            sourceReadRevisionCount: 2,
            AggregateReadinessState.Partial,
            "late-public-reference-events");

        Assert.Null(metrics.Counts);
        Assert.Equal(AggregateReadinessState.Partial, metrics.Readiness);
        Assert.Equal("late-public-reference-events", metrics.UnavailableReason);
    }

    [Fact]
    public void CompleteAggregateMayCarryObservedZeroCounts()
    {
        var counts = InteractionCounts.Create(0, 0, 0, 0, 0, 0, 0, 0, 0);
        var metrics = DailyListingMetrics.Complete(
            new DateOnly(2026, 8, 4),
            "berlin-recording-services",
            Guid.Parse("0198a100-0000-7000-8000-000000000011"),
            new string('d', 64),
            sourceReadRevisionCount: 0,
            counts);

        Assert.Same(counts, metrics.Counts);
        Assert.Equal(AggregateReadinessState.Complete, metrics.Readiness);
        Assert.Null(metrics.UnavailableReason);
    }

    [Fact]
    public void NegativeMetricIsRejected()
    {
        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            InteractionCounts.Create(-1, 0, 0, 0, 0, 0, 0, 0, 0));

        Assert.Equal("ANALYTICS_METRIC_NEGATIVE", exception.Code);
    }

    private static InteractionEvent CreateEvent(
        InteractionEventKind kind,
        Guid? listingId,
        PlacementContext placementContext) =>
        InteractionEvent.CreateAccepted(
            Guid.Parse("0198a100-0000-7000-8000-000000000012"),
            Guid.Parse("0198a100-0000-7000-8000-000000000013"),
            kind,
            "berlin-recording-services",
            listingId,
            Guid.Parse("0198a100-0000-7000-8000-000000000014"),
            ReceivedAtUtc,
            ReceivedAtUtc,
            "catalog_results",
            placementContext,
            ReferrerClass.Internal,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ConsentMode.EssentialOnly,
            new string('e', 64));
}
