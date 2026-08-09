using Microsoft.Extensions.DependencyInjection;
using Platform.Messaging;

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

    public static IServiceCollection AddQueryWorker(
        this IServiceCollection services,
        QueryWorkerOptions publicationOptions,
        QueryPromotionWorkerOptions promotionOptions,
        QueryVisibilityWorkerOptions visibilityOptions,
        QueryOutboxWorkerOptions outboxOptions)
    {
        ArgumentNullException.ThrowIfNull(outboxOptions);
        outboxOptions.Validate();
        services.AddQueryWorker(
            publicationOptions,
            promotionOptions,
            visibilityOptions);
        services.AddSingleton(outboxOptions);
        services.AddSingleton(outboxOptions.CreateDispatcherOptions());
        services.AddSingleton(outboxOptions.CreatePublisherOptions());
        services.AddSingleton<RabbitMqEventPublisher>();
        services.AddSingleton<IIntegrationEventPublisher>(serviceProvider =>
            serviceProvider.GetRequiredService<RabbitMqEventPublisher>());
        services.AddSingleton<PostgresOutboxDispatcher>();
        services.AddHostedService<QueryOutboxWorker>();
        return services;
    }
}
