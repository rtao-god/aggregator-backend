using Microsoft.Extensions.Configuration;
using Platform.Messaging;

namespace Aggregator.Catalog.Worker;

/// <summary>Owns the validated runtime contract for Catalog outbox delivery.</summary>
public sealed record CatalogWorkerOptions
{
    private const int DefaultBatchSize = 50;
    private const int DefaultMaximumDeliveryAttempts = 8;
    private const int DefaultLeaseDurationSeconds = 120;
    private const int DefaultEmptyDelayMilliseconds = 2000;

    public required string ConnectionString { get; init; }

    public required Uri BrokerUri { get; init; }

    public required string Exchange { get; init; }

    public required string DispatcherIdentity { get; init; }

    public int BatchSize { get; init; } = DefaultBatchSize;

    public int MaximumDeliveryAttempts { get; init; } = DefaultMaximumDeliveryAttempts;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(DefaultLeaseDurationSeconds);

    public TimeSpan EmptyDelay { get; init; } = TimeSpan.FromMilliseconds(DefaultEmptyDelayMilliseconds);

    public static CatalogWorkerOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Catalog")
            ?? throw new InvalidOperationException("Connection string 'Catalog' is required.");
        var brokerUriValue = RequireSetting(configuration, "Messaging:BrokerUri");
        if (!Uri.TryCreate(brokerUriValue, UriKind.Absolute, out var brokerUri))
        {
            throw new InvalidOperationException("Messaging:BrokerUri must be an absolute URI.");
        }

        return Create(
            connectionString,
            brokerUri,
            RequireSetting(configuration, "Messaging:Exchange"),
            RequireSetting(configuration, "CatalogWorker:DispatcherIdentity"),
            ReadInt(configuration, "CatalogWorker:BatchSize", DefaultBatchSize),
            ReadInt(
                configuration,
                "CatalogWorker:MaximumDeliveryAttempts",
                DefaultMaximumDeliveryAttempts),
            TimeSpan.FromSeconds(ReadInt(
                configuration,
                "CatalogWorker:LeaseDurationSeconds",
                DefaultLeaseDurationSeconds)),
            TimeSpan.FromMilliseconds(ReadInt(
                configuration,
                "CatalogWorker:EmptyDelayMilliseconds",
                DefaultEmptyDelayMilliseconds)));
    }

    public static CatalogWorkerOptions Create(
        string connectionString,
        Uri brokerUri,
        string exchange,
        string dispatcherIdentity,
        int batchSize = DefaultBatchSize,
        int maximumDeliveryAttempts = DefaultMaximumDeliveryAttempts,
        TimeSpan? leaseDuration = null,
        TimeSpan? emptyDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(brokerUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatcherIdentity);
        var options = new CatalogWorkerOptions
        {
            ConnectionString = connectionString.Trim(),
            BrokerUri = brokerUri,
            Exchange = exchange.Trim(),
            DispatcherIdentity = dispatcherIdentity.Trim(),
            BatchSize = batchSize,
            MaximumDeliveryAttempts = maximumDeliveryAttempts,
            LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(DefaultLeaseDurationSeconds),
            EmptyDelay = emptyDelay ?? TimeSpan.FromMilliseconds(DefaultEmptyDelayMilliseconds),
        };
        options.CreateOutboxDispatcherOptions().Validate();
        options.CreateRabbitMqPublisherOptions().Validate();
        return options;
    }

    public OutboxDispatcherOptions CreateOutboxDispatcherOptions() =>
        new()
        {
            ConnectionString = ConnectionString,
            Schema = "catalog",
            DispatcherIdentity = DispatcherIdentity,
            BatchSize = BatchSize,
            MaximumDeliveryAttempts = MaximumDeliveryAttempts,
            LeaseDuration = LeaseDuration,
            EmptyDelay = EmptyDelay,
        };

    public RabbitMqPublisherOptions CreateRabbitMqPublisherOptions() =>
        new()
        {
            BrokerUri = BrokerUri,
            Exchange = Exchange,
            ClientProvidedName = DispatcherIdentity,
        };

    private static string RequireSetting(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value.Trim()
            : throw new InvalidOperationException($"Configuration value '{key}' is required.");

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue)
    {
        var value = configuration[key];
        if (value is null)
        {
            return defaultValue;
        }

        return int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Configuration value '{key}' must be an integer.");
    }
}
