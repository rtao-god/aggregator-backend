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
    public void ExplicitCatalogAccessQueueSettingsPreserveSharedTransportIdentity()
    {
        using var host = Program.CreateHost(
        [
            "--ConnectionStrings:Analytics=Host=localhost;Database=analytics_db;Username=analytics_app;Password=test",
            "--Analytics:PublicReadProjection:BrokerUri=amqps://broker.example",
            "--Analytics:ListingAccessProjection:BrokerUri=amqps://broker.example",
            "--Analytics:ListingAccessProjection:Queue=analytics.custom-access-projection",
        ]);

        var publicReadOptions = host.Services
            .GetRequiredService<AnalyticsPublicReadProjectionWorkerOptions>();
        var listingAccessOptions = host.Services
            .GetRequiredService<AnalyticsListingAccessProjectionWorkerOptions>();

        Assert.Equal(publicReadOptions.BrokerUri, listingAccessOptions.BrokerUri);
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
