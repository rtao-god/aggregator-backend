using Aggregator.Analytics.Worker;
using Aggregator.Query.Contracts;

namespace Analytics.Worker.Tests;

public sealed class AnalyticsPublicReadProjectionWorkerOptionsTests
{
    [Fact]
    public void ValidConsumerContractIsAccepted()
    {
        var options = CreateOptions();

        options.Validate();

        Assert.Equal(
            QueryIntegrationEventTypes.PublicReadRevisionActivated,
            options.RoutingKey);
    }

    [Fact]
    public void NonAmqpBrokerUriIsRejected()
    {
        var options = CreateOptions() with
        {
            BrokerUri = new Uri("https://broker.example"),
        };

        _ = Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ConsumerCannotBindAnotherProducerRoutingKey()
    {
        var options = CreateOptions() with
        {
            RoutingKey = "query.unsupported",
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

    private static AnalyticsPublicReadProjectionWorkerOptions CreateOptions() =>
        new()
        {
            BrokerUri = new Uri("amqps://broker.example"),
        };
}
