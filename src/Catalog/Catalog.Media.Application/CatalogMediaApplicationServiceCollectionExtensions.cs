using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Catalog.Media.Application;

public static class CatalogMediaApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogMediaApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<CatalogMediaCommandService>();
        services.AddScoped<CatalogMediaProcessingService>();
        return services;
    }
}
