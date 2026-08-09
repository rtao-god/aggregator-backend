using Aggregator.Ingestion.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Ingestion.Infrastructure;

public static class IngestionInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddIngestionInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Ingestion");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'Ingestion' is required for the Ingestion persistence boundary.");
        }

        services.AddDbContext<IngestionDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddScoped<IIngestionBatchRegistrationRepository, EfIngestionBatchLifecycleRepository>();
        services.AddScoped<IIngestionPayloadUploadSessionRepository, EfIngestionBatchLifecycleRepository>();
        services.AddScoped<IIngestionProducerRegistry, IngestionProducerRegistry>();
        services.AddScoped<
            IIngestionProducerRegistrationStore,
            PostgresIngestionProducerRegistrationStore>();
        services.AddScoped<
            ICatalogIngestionReferenceReader,
            PostgresCatalogIngestionReferenceReader>();
        services.AddScoped<IIngestionReferenceEpochReader, EfIngestionReferenceEpochReader>();
        services.AddScoped<IIngestionCommandIdempotencyStore, EfIngestionBatchLifecycleRepository>();
        services.AddScoped<IIngestionRepository, EfIngestionRepository>();
        services.AddScoped<IIngestionProcessingRepository, IngestionProcessingRepository>();
        services.AddScoped<IIngestionCatalogDeliveryReader, PostgresIngestionCatalogDeliveryReader>();
        services.AddScoped<
            ICatalogConfigurationProjectionStore,
            PostgresCatalogConfigurationProjectionStore>();
        services.AddSingleton<IIngestionClock, SystemIngestionClock>();
        return services;
    }
}
