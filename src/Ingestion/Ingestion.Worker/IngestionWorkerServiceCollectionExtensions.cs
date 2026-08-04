using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Ingestion.Worker;

public static class IngestionWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddIngestionWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<IngestionPackageWorkerService>();
        return services;
    }
}
