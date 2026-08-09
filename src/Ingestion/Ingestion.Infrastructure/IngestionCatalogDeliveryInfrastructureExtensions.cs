using Aggregator.Ingestion.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Ingestion.Infrastructure;

public static class IngestionCatalogDeliveryInfrastructureExtensions
{
    public static IServiceCollection AddIngestionCatalogDeliveryInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = IngestionCatalogCommandClientOptions.FromConfiguration(configuration);
        services.AddSingleton(options);
        services.AddHttpClient(
            IngestionCatalogCommandClientOptions.CommandClientName,
            client =>
            {
                client.BaseAddress = options.BaseAddress;
                client.Timeout = options.RequestTimeout;
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = options.RequestTimeout,
            });
        services.AddHttpClient(
            IngestionCatalogCommandClientOptions.TokenClientName,
            client => client.Timeout = options.RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = options.RequestTimeout,
            });
        services.AddSingleton<IngestionCatalogAccessTokenProvider>();
        services.AddScoped<IIngestionCatalogCommandClient, IngestionCatalogCommandClient>();
        services.AddSingleton<
            IIngestionCatalogDeliveryFailureClassifier,
            IngestionCatalogDeliveryFailureClassifier>();
        services.AddScoped<IIngestionCatalogDeliveryStore, PostgresIngestionCatalogDeliveryStore>();
        return services;
    }
}
