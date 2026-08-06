using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Query.Worker;

public static class QueryWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddQueryWorker(
        this IServiceCollection services,
        QueryWorkerOptions publicationOptions,
        QueryPromotionWorkerOptions promotionOptions,
        QueryVisibilityWorkerOptions visibilityOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(publicationOptions);
        ArgumentNullException.ThrowIfNull(promotionOptions);
        ArgumentNullException.ThrowIfNull(visibilityOptions);
        publicationOptions.Validate();
        promotionOptions.Validate();
        visibilityOptions.Validate();

        services.AddSingleton(publicationOptions);
        services.AddSingleton(promotionOptions);
        services.AddSingleton(visibilityOptions);
        services.AddHostedService<CatalogPublicationProjectionWorker>();
        services.AddHostedService<PromotionOverlayProjectionWorker>();
        services.AddHostedService<VisibilitySafetyProjectionWorker>();
        return services;
    }
}
