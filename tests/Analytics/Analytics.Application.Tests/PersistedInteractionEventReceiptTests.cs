using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;

namespace Analytics.Application.Tests;

public sealed class PersistedInteractionEventReceiptTests
{
    private static readonly DateTimeOffset ReceivedAtUtc =
        new(2026, 8, 11, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FromDomainPreservesExactIdempotencyAndResponseIdentity()
    {
        var eventId = Guid.Parse("0198ff00-0000-7000-8000-000000000001");
        var clientEventId = Guid.Parse("0198ff00-0000-7000-8000-000000000002");
        var publicReadRevisionId = Guid.Parse("0198ff00-0000-7000-8000-000000000003");
        var listingId = Guid.Parse("0198ff00-0000-7000-8000-000000000004");
        var interactionEvent = InteractionEvent.CreateAccepted(
            eventId,
            clientEventId,
            InteractionEventKind.ListingImpression,
            "berlin-recording-services",
            listingId,
            publicReadRevisionId,
            ReceivedAtUtc.AddMinutes(-1),
            ReceivedAtUtc,
            "search_results",
            PlacementContext.Create(
                PlacementExposureKind.Organic,
                placementId: null,
                scopeKey: null),
            ReferrerClass.Search,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["utm_source"] = "newsletter",
            },
            ConsentMode.AnalyticsAllowed,
            new string('a', 64));

        var receipt = PersistedInteractionEventReceipt.FromDomain(interactionEvent);

        Assert.Equal(eventId, receipt.EventId);
        Assert.Equal(clientEventId, receipt.SemanticKey.ClientEventId);
        Assert.Equal(InteractionEventKind.ListingImpression, receipt.SemanticKey.Kind);
        Assert.Equal(new string('a', 64), receipt.PayloadDigest);
        Assert.Equal(TrafficQualityState.Accepted, receipt.QualityState);
        Assert.Equal(ReceivedAtUtc, receipt.ReceivedAtUtc);
        Assert.Equal(publicReadRevisionId, receipt.PublicReadRevisionId);
        Assert.Equal(listingId, receipt.ListingId);
    }

    [Fact]
    public void CreateRejectsNonCanonicalPayloadDigest()
    {
        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            PersistedInteractionEventReceipt.Create(
                Guid.Parse("0198ff00-0000-7000-8000-000000000005"),
                InteractionEventSemanticKey.Create(
                    Guid.Parse("0198ff00-0000-7000-8000-000000000006"),
                    InteractionEventKind.ListingOpened),
                new string('A', 64),
                TrafficQualityState.Accepted,
                ReceivedAtUtc,
                Guid.Parse("0198ff00-0000-7000-8000-000000000007"),
                Guid.Parse("0198ff00-0000-7000-8000-000000000008")));

        Assert.Equal("ANALYTICS_EVENT_RECEIPT_DIGEST_INVALID", exception.Code);
    }

    [Fact]
    public void ReceiptCannotExposeMinimizableRawContext()
    {
        var propertyNames = typeof(PersistedInteractionEventReceipt)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            nameof(PersistedInteractionEventReceipt.EventId),
            nameof(PersistedInteractionEventReceipt.ListingId),
            nameof(PersistedInteractionEventReceipt.PayloadDigest),
            nameof(PersistedInteractionEventReceipt.PublicReadRevisionId),
            nameof(PersistedInteractionEventReceipt.QualityState),
            nameof(PersistedInteractionEventReceipt.ReceivedAtUtc),
            nameof(PersistedInteractionEventReceipt.SemanticKey),
        ],
            propertyNames);
        Assert.DoesNotContain("PageContext", propertyNames);
        Assert.DoesNotContain("CampaignParameters", propertyNames);
        Assert.DoesNotContain("PlacementContext", propertyNames);
    }
}
