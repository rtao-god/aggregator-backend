using Microsoft.Extensions.DependencyInjection;
using Platform.Messaging;

namespace Aggregator.Analytics.Worker;

public static class AnalyticsOutboxWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddAnalyticsOutboxWorker(
        this IServiceCollection services,
        AnalyticsOutboxWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton(options.CreateDispatcherOptions());
        services.AddSingleton(options.CreatePublisherOptions());
        services.AddSingleton<RabbitMqEventPublisher>();
        services.AddSingleton<IIntegrationEventPublisher>(serviceProvider =>
            serviceProvider.GetRequiredService<RabbitMqEventPublisher>());
        services.AddSingleton<PostgresOutboxDispatcher>();
        services.AddHostedService<AnalyticsOutboxWorker>();
        return services;
    }
}
