using Aggregator.Ingestion.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Ingestion.Infrastructure;

public static class IngestionCatalogDeliveryQueryInfrastructureExtensions
{
    public static IServiceCollection AddIngestionCatalogDeliveryQueryInfrastructure(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IIngestionCatalogDeliveryReader, PostgresIngestionCatalogDeliveryReader>();
        return services;
    }
}
