using Aggregator.Ingestion.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.ObjectStorage;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>Composes the strict S3-compatible object-store adapter used by Ingestion.</summary>
public static class IngestionObjectStorageServiceCollectionExtensions
{
    public static IServiceCollection AddIngestionObjectStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetRequiredSection("Ingestion:ObjectStorage");
        var options = new S3ObjectStoreOptions
        {
            ServiceUrl = ReadAbsoluteUri(section, "ServiceUrl"),
            Region = ReadRequired(section, "Region"),
            Bucket = ReadRequired(section, "Bucket"),
            AccessKey = ReadRequired(section, "AccessKey"),
            SecretKey = ReadRequired(section, "SecretKey"),
            ForcePathStyle = ReadBoolean(section, "ForcePathStyle"),
        };
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<IObjectStore>(provider =>
  new S3ObjectStore(provider.GetRequiredService<S3ObjectStoreOptions>()));
        services.AddScoped<IIngestionPayloadStore, ObjectStoreIngestionPayloadStore>();
        return services;
    }

    private static Uri ReadAbsoluteUri(IConfigurationSection section, string key)
    {
        var value = ReadRequired(section, key);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var result))
        {
            throw InvalidConfiguration(section, key, "an absolute URI");
        }

        return result;
    }

    private static bool ReadBoolean(IConfigurationSection section, string key)
    {
        var value = ReadRequired(section, key);
        if (!bool.TryParse(value, out var result))
        {
            throw InvalidConfiguration(section, key, "a Boolean");
        }

        return result;
    }

    private static string ReadRequired(IConfigurationSection section, string key)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidConfiguration(section, key, "a non-empty value");
        }

        return value;
    }

    private static InvalidOperationException InvalidConfiguration(
        IConfigurationSection section,
        string key,
        string expected) =>
        new($"Configuration '{section.Path}:{key}' must be {expected}.");
}
