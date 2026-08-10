using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;

namespace Analytics.Application.Tests;

public sealed class PromotionUsageOutboxMessageFactoryTests
{
    private static readonly DateTimeOffset StartsAtUtc =
        new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndsAtUtc = StartsAtUtc.AddDays(1);
    private static readonly Guid EventId =
        Guid.Parse("0198ffa0-0000-7000-8000-000000000001");
    private static readonly Guid RunId =
        Guid.Parse("0198ffa0-0000-7000-8000-000000000002");

    [Fact]
    public void FactoryBindsExactPayloadBytesAndTransportIdentity()
    {
        var message = PromotionUsageOutboxMessageFactory.Create(
            CreateWindow(),
            EventId,
            EndsAtUtc.AddMinutes(5),
            "analytics-aggregation:0198ffa0-0000-7000-8000-000000000002",
            RunId);

        Assert.Equal(EventId, message.MessageId);
        Assert.Equal(
            AnalyticsPromotionUsageIntegrationContracts.RoutingKey,
            message.RoutingKey);
        Assert.Equal(
            AnalyticsPromotionUsageIntegrationContracts.ContractIdentity,
            message.ContractIdentity);
        Assert.Equal(RunId, message.CausationId);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(message.PayloadJson))),
            message.PayloadDigest);

        var payload = JsonSerializer.Deserialize<PromotionUsageWindowClosed>(
            message.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(EventId, payload!.EventId);
        Assert.Equal(CreateWindow().UsageWindowId, payload.UsageWindowId);
        Assert.Equal(4, payload.AggregateRevision);
    }

    [Fact]
    public void InvalidCorrelationIsRejectedBeforeSerialization()
    {
        var exception = Assert.Throws<AnalyticsCommandException>(() =>
            PromotionUsageOutboxMessageFactory.Create(
                CreateWindow(),
                EventId,
                EndsAtUtc,
                " invalid ",
                RunId));

        Assert.Equal("ANALYTICS_PROMOTION_USAGE_CORRELATION_INVALID", exception.Code);
    }

    private static ClosedPromotionUsageWindow CreateWindow() =>
        new(
            Guid.Parse("0198ffa0-0000-7000-8000-000000000003"),
            Guid.Parse("0198ffa0-0000-7000-8000-000000000004"),
            Guid.Parse("0198ffa0-0000-7000-8000-000000000005"),
            "berlin-recording-services",
            StartsAtUtc,
            EndsAtUtc,
            AcceptedImpressions: 9,
            AcceptedListingOpens: 3,
            AcceptedOutboundClicks: 1,
            RunId,
            AggregateRevision: 4);
}
