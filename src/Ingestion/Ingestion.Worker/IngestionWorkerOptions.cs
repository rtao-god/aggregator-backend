using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Aggregator.Ingestion.Worker;

/// <summary>Fail-fast bounded execution settings for Ingestion validation and Catalog delivery.</summary>
public sealed record IngestionWorkerOptions
{
    public const string SectionName = "IngestionWorker";

    public required string WorkerIdentity { get; init; }

    public required int ValidationBatchSize { get; init; }

    public required TimeSpan LeaseDuration { get; init; }

    public required TimeSpan EmptyDelay { get; init; }

    public required string CatalogDeliveryWorkerIdentity { get; init; }

    public required int CatalogDeliveryBatchSize { get; init; }

    public required TimeSpan CatalogDeliveryLeaseDuration { get; init; }

    public required int CatalogDeliveryMaximumAttempts { get; init; }

    public required TimeSpan CatalogDeliveryEmptyDelay { get; init; }

    public static IngestionWorkerOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetRequiredSection(SectionName);
        var options = new IngestionWorkerOptions
        {
            WorkerIdentity = ReadRequired(section, nameof(WorkerIdentity)),
            ValidationBatchSize = ReadInt32(section, nameof(ValidationBatchSize)),
            LeaseDuration = ReadTimeSpan(section, nameof(LeaseDuration)),
            EmptyDelay = ReadTimeSpan(section, nameof(EmptyDelay)),
            CatalogDeliveryWorkerIdentity = ReadRequired(section, nameof(CatalogDeliveryWorkerIdentity)),
            CatalogDeliveryBatchSize = ReadInt32(section, nameof(CatalogDeliveryBatchSize)),
            CatalogDeliveryLeaseDuration = ReadTimeSpan(section, nameof(CatalogDeliveryLeaseDuration)),
            CatalogDeliveryMaximumAttempts = ReadInt32(section, nameof(CatalogDeliveryMaximumAttempts)),
            CatalogDeliveryEmptyDelay = ReadTimeSpan(section, nameof(CatalogDeliveryEmptyDelay)),
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        ValidateIdentity(WorkerIdentity, nameof(WorkerIdentity));
        ValidateIdentity(CatalogDeliveryWorkerIdentity, nameof(CatalogDeliveryWorkerIdentity));
        ValidateBatchSize(ValidationBatchSize, nameof(ValidationBatchSize));
        ValidateBatchSize(CatalogDeliveryBatchSize, nameof(CatalogDeliveryBatchSize));
        ValidateLease(LeaseDuration, nameof(LeaseDuration));
        ValidateLease(CatalogDeliveryLeaseDuration, nameof(CatalogDeliveryLeaseDuration));
        ValidateDelay(EmptyDelay, nameof(EmptyDelay));
        ValidateDelay(CatalogDeliveryEmptyDelay, nameof(CatalogDeliveryEmptyDelay));
        if (CatalogDeliveryMaximumAttempts is < 1 or > 100)
        {
            throw InvalidConfiguration(
                nameof(CatalogDeliveryMaximumAttempts),
                "an integer between 1 and 100");
        }
    }

    private static void ValidateIdentity(string value, string key)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            value.Any(char.IsControl))
        {
            throw InvalidConfiguration(key, "a stable non-empty identity of at most 200 characters");
        }
    }

    private static void ValidateBatchSize(int value, string key)
    {
        if (value is < 1 or > 100)
        {
            throw InvalidConfiguration(key, "an integer between 1 and 100");
        }
    }

    private static void ValidateLease(TimeSpan value, string key)
    {
        if (value < TimeSpan.FromSeconds(10) || value > TimeSpan.FromMinutes(15))
        {
            throw InvalidConfiguration(key, "a duration between ten seconds and fifteen minutes");
        }
    }

    private static void ValidateDelay(TimeSpan value, string key)
    {
        if (value < TimeSpan.FromMilliseconds(100) || value > TimeSpan.FromMinutes(1))
        {
            throw InvalidConfiguration(key, "a duration between 100 milliseconds and one minute");
        }
    }

    private static int ReadInt32(IConfigurationSection section, string key)
    {
        var value = ReadRequired(section, key);
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var result))
        {
            throw InvalidConfiguration(key, "an integer");
        }

        return result;
    }

    private static TimeSpan ReadTimeSpan(IConfigurationSection section, string key)
    {
        var value = ReadRequired(section, key);
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result))
        {
            throw InvalidConfiguration(key, "a TimeSpan");
        }

        return result;
    }

    private static string ReadRequired(IConfigurationSection section, string key)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidConfiguration(key, "a non-empty value");
        }

        return value.Trim();
    }

    private static InvalidOperationException InvalidConfiguration(string key, string expected) =>
        new($"Configuration '{SectionName}:{key}' must be {expected}.");
}
