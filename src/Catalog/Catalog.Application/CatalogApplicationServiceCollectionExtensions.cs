using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Catalog.Application;

public static class CatalogApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<CatalogConfigurationService>();
        services.AddScoped<CatalogListingService>();
        services.AddScoped<CatalogPublicationService>();
        services.AddScoped<CatalogClaimService>();
        services.AddScoped<CatalogVisibilitySuppressionService>();
        return services;
    }
}
