using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aggregator.Analytics.Contracts;
using Aggregator.Promotion.Application;
using Aggregator.Promotion.Worker;

namespace Promotion.Worker.Tests;

public sealed class PromotionUsageProjectionWorkerContractTests
{
    private static readonly Guid EventId =
        Guid.Parse("0198ffc0-0000-7000-8000-000000000001");
    private static readonly Guid CausationId =
        Guid.Parse("0198ffc0-0000-7000-8000-000000000002");

    [Fact]
    public void ExactPayloadDigestIsAccepted()
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"eventId\":\"0198ffc0-0000-7000-8000-000000000001\"}");
        var digest = Convert.ToHexStringLower(SHA256.HashData(payload));

        PromotionUsageProjectionWorker.VerifyPayloadIntegrity(payload, digest);
    }

    [Fact]
    public void PayloadDigestMismatchIsRejected()
    {
        var payload = Encoding.UTF8.GetBytes("{\"value\":1}");

        _ = Assert.Throws<JsonException>(() =>
            PromotionUsageProjectionWorker.VerifyPayloadIntegrity(
                payload,
                new string('a', 64)));
    }

    [Fact]
    public void BrokerMessageIdentityMustMatchProducerEvent()
    {
        Assert.Equal(
            EventId,
            PromotionUsageProjectionWorker.ValidateMessageIdentity(
                EventId,
                EventId.ToString("D")));
        _ = Assert.Throws<JsonException>(() =>
            PromotionUsageProjectionWorker.ValidateMessageIdentity(
                EventId,
                Guid.Parse("0198ffc0-0000-7000-8000-000000000099").ToString("D")));
    }

    [Fact]
    public void ProducerPayloadMapsWithoutRecomputingAnalyticsMeaning()
    {
        var integrationEvent = CreateEvent();

        var message = PromotionUsageProjectionWorker.CreateProjectionMessage(
            integrationEvent,
            EventId,
            AnalyticsPromotionUsageIntegrationContracts.ContractIdentity,
            new string('a', 64),
            "analytics-aggregation:0198ffc0-0000-7000-8000-000000000002",
            CausationId);

        Assert.Equal(integrationEvent.EventId, message.EventId);
        Assert.Equal(integrationEvent.UsageWindowId, message.UsageWindowId);
        Assert.Equal(integrationEvent.PlacementId, message.PlacementId);
        Assert.Equal(integrationEvent.ListingId, message.ListingId);
        Assert.Equal(integrationEvent.CatalogKey, message.CatalogKey);
        Assert.Equal(integrationEvent.AcceptedImpressions, message.AcceptedImpressions);
        Assert.Equal(integrationEvent.AcceptedListingOpens, message.AcceptedListingOpens);
        Assert.Equal(integrationEvent.AcceptedOutboundClicks, message.AcceptedOutboundClicks);
        Assert.Equal(integrationEvent.AggregationRunId, message.AggregationRunId);
        Assert.Equal(integrationEvent.AggregateRevision, message.AggregateRevision);
        Assert.Equal(CausationId, message.CausationId);
    }

    [Fact]
    public void OnlyUnavailableOrTransientFailuresAreRetryable()
    {
        var unavailable = new PromotionApplicationException(
            "Promotion.Usage",
            "PROMOTION_USAGE_STORE_UNAVAILABLE",
            503,
            "Promotion usage store is unavailable.",
            "Retry after restoring the Promotion database.");
        var gap = new PromotionApplicationException(
            "Promotion.Usage",
            "PROMOTION_USAGE_REVISION_GAP",
            503,
            "Promotion usage revision is missing.",
            "Replay the missing exact revision.");
        var stale = new PromotionApplicationException(
            "Promotion.Usage",
            "PROMOTION_USAGE_REVISION_STALE",
            409,
            "Promotion usage revision is stale.",
            "Discard the stale revision.");

        Assert.True(PromotionUsageProjectionWorker.IsRetryable(unavailable));
        Assert.True(PromotionUsageProjectionWorker.IsRetryable(gap));
        Assert.False(PromotionUsageProjectionWorker.IsRetryable(stale));
        Assert.True(PromotionUsageProjectionWorker.IsRetryable(new TimeoutException()));
    }

    private static PromotionUsageWindowClosed CreateEvent() =>
        new(
            EventId,
            Guid.Parse("0198ffc0-0000-7000-8000-000000000003"),
            Guid.Parse("0198ffc0-0000-7000-8000-000000000004"),
            Guid.Parse("0198ffc0-0000-7000-8000-000000000005"),
            "berlin-recording-services",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            AcceptedImpressions: 10,
            AcceptedListingOpens: 4,
            AcceptedOutboundClicks: 2,
            CausationId,
            AggregateRevision: 3,
            new DateTimeOffset(2026, 8, 10, 0, 5, 0, TimeSpan.Zero));
}
