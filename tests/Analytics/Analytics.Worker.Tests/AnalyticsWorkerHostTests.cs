using Aggregator.Analytics.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Analytics.Worker.Tests;

public sealed class AnalyticsWorkerHostTests
{
    [Fact]
    public void HostRegistersAggregationAndPublicReferenceWorkers()
    {
        using var host = Program.CreateHost(
        [
            "--ConnectionStrings:Analytics=Host=localhost;Database=analytics_db;Username=analytics_app;Password=test",
            "--Analytics:PublicReadProjection:BrokerUri=amqp://broker.example",
        ]);

        var hostedServices = host.Services.GetServices<IHostedService>().ToArray();

        Assert.Contains(
            hostedServices,
            service => service is AnalyticsPublicReadProjectionWorker);
        Assert.Contains(
            hostedServices,
            service => string.Equals(
                service.GetType().Name,
                "AnalyticsAggregationWorker",
                StringComparison.Ordinal));
    }

    [Fact]
    public void MissingPublicReferenceBrokerConfigurationFailsAtStartup()
    {
        _ = Assert.Throws<InvalidOperationException>(() => Program.CreateHost(
        [
            "--ConnectionStrings:Analytics=Host=localhost;Database=analytics_db;Username=analytics_app;Password=test",
        ]));
    }
}
