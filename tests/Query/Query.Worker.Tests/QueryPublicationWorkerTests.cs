using Aggregator.Query.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Query.Worker.Tests;

public sealed class QueryPublicationWorkerTests
{
    [Fact]
    public void ValidPublicationWorkerOptionsAreAccepted()
    {
        var options = CreatePublicationOptions();

        options.Validate();

        Assert.Equal((ushort)16, options.PrefetchCount);
        Assert.Equal("catalog.publication.activated", options.RoutingKey);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)257)]
    public void UnsafePublicationPrefetchIsRejected(ushort prefetchCount)
    {
        var options = CreatePublicationOptions() with
        {
            PrefetchCount = prefetchCount,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("https://rabbitmq.example")]
    [InlineData("file:///tmp/rabbitmq")]
    public void NonAmqpPublicationBrokerIsRejected(string brokerUri)
    {
        var options = CreatePublicationOptions() with
        {
            BrokerUri = new Uri(brokerUri),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ActiveCompositionRegistersOnlyTheCurrentPublicationAndPromotionWorkers()
    {
        var services = new ServiceCollection();

        services.AddQueryWorker(
            CreatePublicationOptions(),
            CreatePromotionOptions());

        var hostedWorkerTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .Where(type => type is not null)
            .ToArray();
        Assert.Equal(
            [typeof(CatalogPublicationProjectionWorker), typeof(PromotionOverlayProjectionWorker)],
            hostedWorkerTypes);
    }

    private static QueryWorkerOptions CreatePublicationOptions() =>
        new()
        {
            BrokerUri = new Uri("amqp://guest:guest@localhost:5672/"),
            Exchange = "aggregator.events",
            RoutingKey = "catalog.publication.activated",
            Queue = "query.catalog-publication-projection",
            PrefetchCount = 16,
        };

    private static QueryPromotionWorkerOptions CreatePromotionOptions() =>
        new()
        {
            BrokerUri = new Uri("amqp://guest:guest@localhost:5672/"),
            Exchange = "aggregator.events",
            Queue = "query.promotion-overlay-projection",
            DeadLetterExchange = "aggregator.dead-letter",
            DeadLetterQueue = "query.promotion-overlay-projection.dead-letter",
            RoutingKey = "promotion.overlay.activated",
            PrefetchCount = 8,
        };
}
