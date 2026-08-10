using Aggregator.Analytics.Contracts;
using Aggregator.Promotion.Worker;

namespace Promotion.Worker.Tests;

public sealed class PromotionUsageProjectionWorkerOptionsTests
{
    [Fact]
    public void ValidProducerContractIsAccepted()
    {
        var options = CreateOptions();

        options.Validate();

        Assert.Equal(
            AnalyticsPromotionUsageIntegrationContracts.RoutingKey,
            options.RoutingKey);
        Assert.Equal("promotion.analytics-usage", options.Queue);
        Assert.Equal("promotion.analytics-usage.dead-letter", options.DeadLetterQueue);
    }

    [Fact]
    public void ConsumerCannotBindAnotherAnalyticsRoutingKey()
    {
        var options = CreateOptions() with
        {
            RoutingKey = "analytics.unsupported",
        };

        _ = Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)65)]
    public void UnsafePrefetchIsRejected(ushort prefetchCount)
    {
        var options = CreateOptions() with { PrefetchCount = prefetchCount };

        _ = Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(101)]
    public void UnsafeDeliveryLimitIsRejected(int deliveryLimit)
    {
        var options = CreateOptions() with { DeliveryLimit = deliveryLimit };

        _ = Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private static PromotionUsageProjectionWorkerOptions CreateOptions() =>
        new()
        {
            BrokerUri = new Uri("amqps://broker.example"),
            Exchange = "aggregator.events",
        };
}
