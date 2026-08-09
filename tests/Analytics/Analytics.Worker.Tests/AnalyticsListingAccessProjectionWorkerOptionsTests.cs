using Aggregator.Analytics.Worker;
using Aggregator.Catalog.Contracts;

namespace Analytics.Worker.Tests;

public sealed class AnalyticsListingAccessProjectionWorkerOptionsTests
{
    [Fact]
    public void ValidCatalogAccessConsumerContractIsAccepted()
    {
        var options = CreateOptions();

        options.Validate();

        Assert.Equal(
            CatalogIntegrationEventTypes.ListingAccessGrantChanged,
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
    public void ConsumerCannotBindAnotherCatalogRoutingKey()
    {
        var options = CreateOptions() with
        {
            RoutingKey = CatalogIntegrationEventTypes.ListingPromotionEligibilityChanged,
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

    [Theory]
    [InlineData(99)]
    [InlineData(60001)]
    public void UnsafeRetryDelayIsRejected(int milliseconds)
    {
        var options = CreateOptions() with
        {
            RetryDelay = TimeSpan.FromMilliseconds(milliseconds),
        };

        _ = Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private static AnalyticsListingAccessProjectionWorkerOptions CreateOptions() =>
        new()
        {
            BrokerUri = new Uri("amqps://broker.example"),
        };
}
