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

    [Fact]
    public void ValidPublicReadOutboxContractIsAccepted()
    {
        var options = new QueryOutboxWorkerOptions
        {
            ConnectionString = "Host=localhost;Database=query_db;Username=query_app;Password=test",
            BrokerUri = new Uri("amqps://broker.example"),
            Exchange = "aggregator.events",
            DispatcherIdentity = "query-public-read-outbox",
            BatchSize = 50,
            MaximumDeliveryAttempts = 8,
            LeaseDuration = TimeSpan.FromMinutes(2),
            EmptyDelay = TimeSpan.FromSeconds(2),
        };

        options.Validate();

        Assert.Equal("messaging", options.CreateDispatcherOptions().Schema);
        Assert.Equal(
            "query-public-read-outbox",
            options.CreatePublisherOptions().ClientProvidedName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void UnsafePublicReadOutboxBatchSizeIsRejected(int batchSize)
    {
        var options = CreateOutboxOptions() with { BatchSize = batchSize };

        _ = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void PublicReadOutboxRejectsNonAmqpBroker()
    {
        var options = CreateOutboxOptions() with
        {
            BrokerUri = new Uri("https://broker.example"),
        };

        _ = Assert.Throws<ArgumentException>(options.Validate);
    }

    private static QueryOutboxWorkerOptions CreateOutboxOptions() =>
        new()
        {
            ConnectionString = "Host=localhost;Database=query_db;Username=query_app;Password=test",
            BrokerUri = new Uri("amqp://broker.example"),
        };
}
