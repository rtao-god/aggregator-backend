using Aggregator.Query.Application;
using Aggregator.Query.Infrastructure;
using Aggregator.Query.Worker;

namespace Query.Worker.Tests;

public sealed class QueryPublicationWorkerTests
{
    [Fact]
    public void ValidWorkerOptionsAreAccepted()
    {
        var options = CreateOptions();

        options.Validate();

        Assert.Equal((ushort)16, options.PrefetchCount);
        Assert.Equal(5, options.MaximumDeliveryAttempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void UnsafeDeliveryLimitIsRejected(int maximumDeliveryAttempts)
    {
        var options = CreateOptions() with
        {
            MaximumDeliveryAttempts = maximumDeliveryAttempts,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("https://rabbitmq.example")]
    [InlineData("file:///tmp/rabbitmq")]
    public void NonAmqpBrokerIsRejected(string brokerUri)
    {
        var options = CreateOptions() with
        {
            BrokerUri = new Uri(brokerUri),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void GeneratedAdapterIsTheOnlyRegisteredPublicationHandlerImplementation()
    {
        Assert.True(typeof(IQueryCatalogPublicationHandler)
            .IsAssignableFrom(typeof(GeneratedCatalogPublicationProjectionAdapter)));
        var publicMethods = typeof(GeneratedCatalogPublicationProjectionAdapter)
            .GetMethods()
            .Where(method => method.DeclaringType == typeof(GeneratedCatalogPublicationProjectionAdapter))
            .Select(method => method.Name)
            .ToArray();
        Assert.Equal(["HandleAsync"], publicMethods);
    }

    private static QueryPublicationWorkerOptions CreateOptions() =>
        new()
        {
            BrokerUri = new Uri("amqp://guest:guest@localhost:5672/"),
            Exchange = "aggregator.events",
            RoutingKey = "catalog.publication-activated",
            Queue = "query.catalog-publication",
            DeadLetterExchange = "aggregator.dead-letter",
            DeadLetterRoutingKey = "query.catalog-publication.failed",
            PrefetchCount = 16,
            MaximumDeliveryAttempts = 5,
            MaximumMessageBytes = 8 * 1024 * 1024,
            RecoveryInterval = TimeSpan.FromSeconds(10),
        };
}
