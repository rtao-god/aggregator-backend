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

    /// <summary>Registers the Catalog configuration event consumer under its own transport contract.</summary>
    public static IServiceCollection AddIngestionCatalogConfigurationProjectionWorker(
        this IServiceCollection services,
        IngestionCatalogConfigurationProjectionWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddHostedService<IngestionCatalogConfigurationProjectionWorker>();
        return services;
    }
}
