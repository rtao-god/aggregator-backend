using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ingestion.Worker.Tests;

public sealed class IngestionCatalogConfigurationProjectionWorkerOptionsTests
{
    [Fact]
    public void ValidConsumerContractIsAccepted()
    {
        var options = CreateOptions();

        options.Validate();

        Assert.Equal(CatalogIntegrationEventTypes.ConfigurationActivated, options.RoutingKey);
    }

    [Fact]
    public void CompositionRegistersOnlyTheCatalogProjectionConsumer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIngestionCatalogConfigurationProjectionWorker(CreateOptions());
        using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().ToArray();

        Assert.Collection(
            hosted,
            service => Assert.IsType<IngestionCatalogConfigurationProjectionWorker>(service));
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
            RoutingKey = "catalog.unsupported",
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

    private static IngestionCatalogConfigurationProjectionWorkerOptions CreateOptions() =>
        new()
        {
            BrokerUri = new Uri("amqps://broker.example"),
        };
}
