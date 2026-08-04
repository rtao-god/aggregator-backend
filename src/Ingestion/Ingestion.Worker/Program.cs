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
        builder.Services
  .AddIngestionApplication()
  .AddIngestionInfrastructure(builder.Configuration)
  .AddIngestionObjectStorage(builder.Configuration)
  .AddIngestionProcessingInfrastructure(builder.Configuration)
  .AddIngestionWorker(options);
        builder.Services.AddPlatformObservability(
  builder.Configuration,
  "ingestion-worker");
        return builder.Build();
    }
}
