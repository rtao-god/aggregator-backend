using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Query.Worker;

public static class QueryWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddQueryWorker(
        this IServiceCollection services,
        QueryWorkerOptions publicationOptions,
        QueryPromotionWorkerOptions promotionOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(publicationOptions);
        ArgumentNullException.ThrowIfNull(promotionOptions);
        publicationOptions.Validate();
        promotionOptions.Validate();

        services.AddSingleton(publicationOptions);
        services.AddSingleton(promotionOptions);
        services.AddHostedService<CatalogPublicationProjectionWorker>();
        services.AddHostedService<PromotionOverlayProjectionWorker>();
        return services;
    }
}
