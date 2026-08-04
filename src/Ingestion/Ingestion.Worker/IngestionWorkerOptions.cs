using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Aggregator.Ingestion.Worker;

/// <summary>Fail-fast bounded execution settings for the canonical Ingestion validation worker.</summary>
public sealed record IngestionWorkerOptions
{
    public const string SectionName = "IngestionWorker";

    public required string WorkerIdentity { get; init; }

    public required int ValidationBatchSize { get; init; }

    public required TimeSpan LeaseDuration { get; init; }

    public required TimeSpan EmptyDelay { get; init; }

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
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkerIdentity) ||
  WorkerIdentity.Length > 200 ||
  WorkerIdentity.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Configuration '{SectionName}:{nameof(WorkerIdentity)}' must be a stable non-empty identity of at most 200 characters.");
        }

        if (ValidationBatchSize is < 1 or > 100)
        {
            throw new InvalidOperationException(
                $"Configuration '{SectionName}:{nameof(ValidationBatchSize)}' must be between 1 and 100.");
        }

        if (LeaseDuration < TimeSpan.FromSeconds(10) ||
  LeaseDuration > TimeSpan.FromMinutes(15))
        {
            throw new InvalidOperationException(
                $"Configuration '{SectionName}:{nameof(LeaseDuration)}' must be between ten seconds and fifteen minutes.");
        }

        if (EmptyDelay < TimeSpan.FromMilliseconds(100) ||
  EmptyDelay > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException(
                $"Configuration '{SectionName}:{nameof(EmptyDelay)}' must be between 100 milliseconds and one minute.");
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
            throw InvalidConfiguration(section, key, "an integer");
        }

        return result;
    }

    private static TimeSpan ReadTimeSpan(IConfigurationSection section, string key)
    {
        var value = ReadRequired(section, key);
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result))
        {
            throw InvalidConfiguration(section, key, "a TimeSpan");
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
