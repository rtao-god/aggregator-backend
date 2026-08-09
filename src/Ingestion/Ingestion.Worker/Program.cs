using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Infrastructure;
using Microsoft.Extensions.Hosting;
using Platform.Observability;

namespace Aggregator.Ingestion.Worker;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var host = CreateHost(args);
        await host.RunAsync();
    }

    public static IHost CreateHost(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = Host.CreateApplicationBuilder(args);
        var options = IngestionWorkerOptions.FromConfiguration(builder.Configuration);
        var projectionOptions = builder.Configuration
            .GetSection(IngestionCatalogConfigurationProjectionWorkerOptions.SectionName)
            .Get<IngestionCatalogConfigurationProjectionWorkerOptions>()
            ?? throw new InvalidOperationException(
                $"Configuration section '{IngestionCatalogConfigurationProjectionWorkerOptions.SectionName}' is required.");
        projectionOptions.Validate();
        builder.Services.AddSingleton(projectionOptions);
        builder.Services
            .AddIngestionApplication()
            .AddIngestionInfrastructure(builder.Configuration)
            .AddIngestionCatalogProjectionInfrastructure()
            .AddIngestionObjectStorage(builder.Configuration)
            .AddIngestionProcessingInfrastructure(builder.Configuration)
            .AddIngestionCatalogDeliveryInfrastructure(builder.Configuration)
            .AddIngestionWorker(options);
        builder.Services.AddPlatformObservability(
            builder.Configuration,
            "ingestion-worker");
        return builder.Build();
    }
}
