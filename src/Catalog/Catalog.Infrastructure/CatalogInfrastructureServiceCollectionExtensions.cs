using Amazon.S3;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Media.Application;
using Aggregator.Catalog.Media.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Messaging;
using Platform.Observability;

namespace Aggregator.Catalog.Infrastructure;

public static class CatalogInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Catalog")
            ?? throw new InvalidOperationException("Connection string 'Catalog' is required.");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Catalog' cannot be empty.");
        }

        services.AddDbContext<CatalogDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddScoped<EfCatalogRepository>();
        services.AddScoped<ICatalogRepository>(serviceProvider =>
            serviceProvider.GetRequiredService<EfCatalogRepository>());
        services.AddScoped<ICatalogConfigurationActivationRepository>(serviceProvider =>
            serviceProvider.GetRequiredService<EfCatalogRepository>());
        services.AddScoped<ICatalogPublicationOperationCommitter>(serviceProvider =>
            serviceProvider.GetRequiredService<EfCatalogRepository>());
        services.AddScoped<ICatalogListingDisputeRepository>(serviceProvider =>
            serviceProvider.GetRequiredService<EfCatalogRepository>());
        services.AddScoped<ICatalogPublicationOperationStore, PostgresCatalogPublicationOperationStore>();
        services.AddScoped<
            ICatalogPublicationOperationFailureClassifier,
            CatalogPublicationOperationFailureClassifier>();
        services.AddScoped<
            ICatalogVisibilitySuppressionRepository,
            PostgresCatalogVisibilitySuppressionRepository>();
        services.AddScoped<CatalogReadinessProbe>();
        services.AddScoped<ICatalogMediaStore, EfCatalogMediaRepository>();
        services.AddScoped<ICatalogMediaPublicationBindingAuthority, CatalogMediaPublicationBindingAuthority>();
        services.AddScoped<ICatalogMediaObjectStore, ObjectStoreCatalogMediaStore>();
        services.AddSingleton<ICatalogMediaObjectKeyFactory, CatalogMediaObjectKeyFactory>();
        services.AddSingleton<IEtagFactory, Sha256EtagFactory>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICatalogIdSource, UuidV7CatalogIdSource>();
        services.AddCatalogPublicationStorage(configuration);
        services.AddCatalogMediaStorage(configuration);
        return services;
    }

    public static IServiceCollection AddCatalogPublicationStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = CatalogPublicationStorageOptions.FromConfiguration(configuration);
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IAmazonS3>(_ => CatalogS3ClientFactory.Create(options));
        services.AddScoped<ICatalogPublicationArtifactStore, S3CatalogPublicationArtifactStore>();
        return services;
    }

    public static IServiceCollection AddCatalogMediaStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = CatalogMediaObjectStorageOptions.FromConfiguration(configuration);
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<CatalogMediaAmazonS3Client>(_ =>
            new CatalogMediaAmazonS3Client(CatalogMediaS3ClientFactory.Create(options)));
        return services;
    }
}

public sealed class UuidV7CatalogIdSource : ICatalogIdSource, ICatalogMediaIdSource
{
    public Guid CreateId() => Guid.CreateVersion7();
}
