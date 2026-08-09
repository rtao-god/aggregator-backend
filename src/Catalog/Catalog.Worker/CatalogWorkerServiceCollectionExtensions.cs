using Microsoft.Extensions.DependencyInjection;
using Platform.Messaging;

namespace Aggregator.Catalog.Worker;

public static class CatalogWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogWorker(
        this IServiceCollection services,
        CatalogWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton(options.CreateRabbitMqPublisherOptions());
        services.AddSingleton(options.CreateOutboxDispatcherOptions());
        services.AddSingleton<RabbitMqEventPublisher>();
        services.AddSingleton<IIntegrationEventPublisher>(provider =>
            provider.GetRequiredService<RabbitMqEventPublisher>());
        services.AddSingleton<PostgresOutboxDispatcher>();
        services.AddHostedService<CatalogOutboxWorker>();
        services.AddHostedService<CatalogPublicationOperationWorker>();
        return services;
    }
}
