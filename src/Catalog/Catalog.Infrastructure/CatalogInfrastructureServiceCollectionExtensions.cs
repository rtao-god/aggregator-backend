using Aggregator.Catalog.Application;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        var storageOptions = ReadObjectStorageOptions(configuration);
        storageOptions.Validate();

        services.AddDbContext<CatalogDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddScoped<ICatalogRepository, EfCatalogRepository>();
        services.AddScoped<
            ICatalogVisibilitySuppressionRepository,
            PostgresCatalogVisibilitySuppressionRepository>();
        services.AddScoped<CatalogReadinessProbe>();
        services.AddSingleton<ICatalogIdSource, UuidV7CatalogIdSource>();
        services.AddSingleton<IOptions<CatalogObjectStorageOptions>>(Options.Create(storageOptions));
        services.AddSingleton<IAmazonS3>(_ =>
        {
            var credentials = new BasicAWSCredentials(storageOptions.AccessKey, storageOptions.SecretKey);
            var clientConfiguration = new AmazonS3Config
            {
                ServiceURL = storageOptions.ServiceUrl,
                ForcePathStyle = storageOptions.ForcePathStyle,
            };
            return new AmazonS3Client(credentials, clientConfiguration);
        });
        services.AddSingleton<ICatalogPublicationArtifactStore, S3CatalogPublicationArtifactStore>();
        return services;
    }

    private static CatalogObjectStorageOptions ReadObjectStorageOptions(IConfiguration configuration)
    {
        var section = configuration.GetRequiredSection(CatalogObjectStorageOptions.SectionName);
        var maximumBytesText = section[nameof(CatalogObjectStorageOptions.MaximumPublicationBytes)];
        var maximumBytes = maximumBytesText is null
            ? 64L * 1024 * 1024
            : long.TryParse(
                maximumBytesText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedMaximumBytes)
                ? parsedMaximumBytes
                : throw new InvalidOperationException(
                    $"'{CatalogObjectStorageOptions.SectionName}:{nameof(CatalogObjectStorageOptions.MaximumPublicationBytes)}' must be an integer.");

        return new CatalogObjectStorageOptions
        {
            ServiceUrl = RequireSetting(section, nameof(CatalogObjectStorageOptions.ServiceUrl)),
            BucketName = RequireSetting(section, nameof(CatalogObjectStorageOptions.BucketName)),
            AccessKey = RequireSetting(section, nameof(CatalogObjectStorageOptions.AccessKey)),
            SecretKey = RequireSetting(section, nameof(CatalogObjectStorageOptions.SecretKey)),
            ForcePathStyle = section.GetValue<bool?>(nameof(CatalogObjectStorageOptions.ForcePathStyle)) ?? true,
            MaximumPublicationBytes = maximumBytes,
        };
    }

    private static string RequireSetting(IConfiguration section, string name) =>
        section[name] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Configuration value '{CatalogObjectStorageOptions.SectionName}:{name}' is required.");
}

public sealed class UuidV7CatalogIdSource : ICatalogIdSource
{
    public Guid CreateId() => Guid.CreateVersion7();
}
