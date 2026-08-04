using Aggregator.Query.Worker;

namespace Query.Worker.Tests;

public sealed class QueryWorkerOptionsTests
{
    [Fact]
    public void ValidTransportContractIsAccepted()
    {
        var options = new QueryWorkerOptions
        {
            BrokerUri = new Uri("amqps://broker.example"),
            Exchange = "aggregator.events",
            Queue = "query.catalog-publication-projection",
            RoutingKey = "catalog.publication.activated",
            PrefetchCount = 8,
        };

        options.Validate();
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)257)]
    public void UnsafePrefetchIsRejected(ushort prefetchCount)
    {
        var options = new QueryWorkerOptions
        {
            BrokerUri = new Uri("amqp://broker.example"),
            PrefetchCount = prefetchCount,
        };

        _ = Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void NonAmqpBrokerUriIsRejected()
    {
        var options = new QueryWorkerOptions
        {
            BrokerUri = new Uri("https://broker.example"),
        };

        _ = Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
