using Aggregator.Catalog.Worker;
using Microsoft.Extensions.Configuration;

namespace Catalog.Worker.Tests;

public sealed class CatalogWorkerOptionsTests
{
    [Fact]
    public void ValidOptionsProduceExactTransportConfiguration()
    {
        var options = CatalogWorkerOptions.Create(
            "Host=localhost;Database=catalog;Username=catalog_app;Password=test",
            new Uri("amqp://guest:guest@localhost:5672/"),
            "platform.events",
            "catalog-worker-test");

        var outbox = options.CreateOutboxDispatcherOptions();
        var publisher = options.CreateRabbitMqPublisherOptions();

        Assert.Equal("catalog", outbox.Schema);
        Assert.Equal(8, outbox.MaximumDeliveryAttempts);
        Assert.Equal("platform.events", publisher.Exchange);
        Assert.Equal("catalog-worker-test", publisher.ClientProvidedName);
        Assert.Equal(5, options.MaximumPublicationAttempts);
        Assert.Equal(TimeSpan.FromMinutes(5), options.PublicationLeaseDuration);
    }

    [Fact]
    public void NonRabbitBrokerUriIsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CatalogWorkerOptions.Create(
                "Host=localhost;Database=catalog;Username=catalog_app;Password=test",
                new Uri("https://broker.test"),
                "platform.events",
                "catalog-worker-test"));

        Assert.Equal("BrokerUri", exception.ParamName);
    }

    [Fact]
    public void MissingRequiredConfigurationFailsBeforeHostBuild()
    {
        var configuration = new ConfigurationManager();
        configuration["ConnectionStrings:Catalog"] =
            "Host=localhost;Database=catalog;Username=catalog_app;Password=test";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CatalogWorkerOptions.FromConfiguration(configuration));

        Assert.Contains("Messaging:BrokerUri", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void InvalidBatchSizeIsRejected(int batchSize)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CatalogWorkerOptions.Create(
                "Host=localhost;Database=catalog;Username=catalog_app;Password=test",
                new Uri("amqp://guest:guest@localhost:5672/"),
                "platform.events",
                "catalog-worker-test",
                batchSize));

        Assert.Equal("BatchSize", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidPublicationAttemptLimitIsRejected(int maximumPublicationAttempts)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CatalogWorkerOptions.Create(
                "Host=localhost;Database=catalog;Username=catalog_app;Password=test",
                new Uri("amqp://guest:guest@localhost:5672/"),
                "platform.events",
                "catalog-worker-test",
                maximumPublicationAttempts: maximumPublicationAttempts));

        Assert.Equal("MaximumPublicationAttempts", exception.ParamName);
    }

    [Fact]
    public void NonPositivePublicationLeaseIsRejected()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CatalogWorkerOptions.Create(
                "Host=localhost;Database=catalog;Username=catalog_app;Password=test",
                new Uri("amqp://guest:guest@localhost:5672/"),
                "platform.events",
                "catalog-worker-test",
                publicationLeaseDuration: TimeSpan.Zero));

        Assert.Equal("PublicationLeaseDuration", exception.ParamName);
    }
}
