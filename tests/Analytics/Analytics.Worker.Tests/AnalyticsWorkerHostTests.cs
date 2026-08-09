using Aggregator.Analytics.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Analytics.Worker.Tests;

public sealed class AnalyticsWorkerHostTests
{
    [Fact]
    public void HostRegistersAggregationPublicReferenceAndCatalogAccessWorkers()
    {
        using var host = Program.CreateHost(
        [
            "--ConnectionStrings:Analytics=Host=localhost;Database=analytics_db;Username=analytics_app;Password=test",
            "--Analytics:PublicReadProjection:BrokerUri=amqp://broker.example",
        ]);

        var hostedServices = host.Services.GetServices<IHostedService>().ToArray();
        var publicReadOptions = host.Services
            .GetRequiredService<AnalyticsPublicReadProjectionWorkerOptions>();
        var listingAccessOptions = host.Services
            .GetRequiredService<AnalyticsListingAccessProjectionWorkerOptions>();

        Assert.Contains(
            hostedServices,
            service => service is AnalyticsPublicReadProjectionWorker);
        Assert.Contains(
            hostedServices,
            service => service is AnalyticsListingAccessProjectionWorker);
        Assert.Contains(
            hostedServices,
            service => string.Equals(
                service.GetType().Name,
                "AnalyticsAggregationWorker",
                StringComparison.Ordinal));
        Assert.Equal(publicReadOptions.BrokerUri, listingAccessOptions.BrokerUri);
        Assert.Equal(publicReadOptions.Exchange, listingAccessOptions.Exchange);
        Assert.Equal(
            publicReadOptions.DeadLetterExchange,
            listingAccessOptions.DeadLetterExchange);
        Assert.NotEqual(publicReadOptions.Queue, listingAccessOptions.Queue);
    }

    [Fact]
    public void CatalogAccessQueueSettingsCannotCreateAnotherBrokerTransport()
    {
        using var host = Program.CreateHost(
        [
            "--ConnectionStrings:Analytics=Host=localhost;Database=analytics_db;Username=analytics_app;Password=test",
            "--Analytics:PublicReadProjection:BrokerUri=amqps://canonical-broker.example",
            "--Analytics:PublicReadProjection:Exchange=canonical.events",
            "--Analytics:PublicReadProjection:DeadLetterExchange=canonical.dead-letter",
            "--Analytics:ListingAccessProjection:BrokerUri=amqp://forbidden-second-broker.example",
            "--Analytics:ListingAccessProjection:Exchange=forbidden.events",
            "--Analytics:ListingAccessProjection:DeadLetterExchange=forbidden.dead-letter",
            "--Analytics:ListingAccessProjection:Queue=analytics.custom-access-projection",
        ]);

        var publicReadOptions = host.Services
            .GetRequiredService<AnalyticsPublicReadProjectionWorkerOptions>();
        var listingAccessOptions = host.Services
            .GetRequiredService<AnalyticsListingAccessProjectionWorkerOptions>();

        Assert.Equal(publicReadOptions.BrokerUri, listingAccessOptions.BrokerUri);
        Assert.Equal(publicReadOptions.Exchange, listingAccessOptions.Exchange);
        Assert.Equal(
            publicReadOptions.DeadLetterExchange,
            listingAccessOptions.DeadLetterExchange);
        Assert.Equal("analytics.custom-access-projection", listingAccessOptions.Queue);
    }

    [Fact]
    public void MissingSharedBrokerConfigurationFailsAtStartup()
    {
        _ = Assert.Throws<InvalidOperationException>(() => Program.CreateHost(
        [
            "--ConnectionStrings:Analytics=Host=localhost;Database=analytics_db;Username=analytics_app;Password=test",
        ]));
    }
}
