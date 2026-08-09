using Aggregator.Ingestion.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Ingestion.Infrastructure;

public static class IngestionCatalogProjectionInfrastructureExtensions
{
    /// <summary>Registers only the worker-owned Catalog event mutation adapter.</summary>
    public static IServiceCollection AddIngestionCatalogProjectionInfrastructure(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<
            ICatalogConfigurationProjectionStore,
            PostgresCatalogConfigurationProjectionStore>();
        return services;
    }
}
