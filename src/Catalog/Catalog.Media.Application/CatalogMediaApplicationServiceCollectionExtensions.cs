using Aggregator.Catalog.Media.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Catalog.Media.Application;

public static class CatalogMediaApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogMediaApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<CatalogMediaCommandService>();
        services.AddScoped<CatalogMediaProcessingService>();
        services.AddScoped<ICatalogMediaPublicationBindingAuthority, CatalogMediaPublicationBindingAuthority>();
        return services;
    }
}
