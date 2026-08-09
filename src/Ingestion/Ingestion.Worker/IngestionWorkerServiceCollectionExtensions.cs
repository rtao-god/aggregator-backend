using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Ingestion.Worker;

public static class IngestionWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddIngestionWorker(
        this IServiceCollection services,
        IngestionWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddHostedService<IngestionValidationWorker>();
        services.AddHostedService<IngestionCatalogDeliveryWorker>();
        return services;
    }
}
