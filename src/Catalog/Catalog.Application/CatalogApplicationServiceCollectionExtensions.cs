using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Catalog.Application;

/// <summary>Registers Catalog application services without infrastructure dependencies.</summary>
public static class CatalogApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<CatalogConfigurationService>();
        services.AddScoped<CatalogListingService>();
        services.AddScoped<CatalogListingDisputeService>();
        services.AddScoped<CatalogPublicationService>();
        services.AddScoped<CatalogPublicationOperationService>();
        services.AddScoped<CatalogPublicationOperationExecutor>();
        services.AddScoped<CatalogClaimService>();
        services.AddScoped<CatalogVisibilitySuppressionService>();
        return services;
    }
}
