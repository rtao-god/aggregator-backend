using Aggregator.Analytics.Contracts;
using Microsoft.Extensions.Configuration;

namespace Aggregator.Promotion.Worker;

/// <summary>Validated RabbitMQ contract for the Analytics Promotion-usage projection consumer.</summary>
public sealed record PromotionUsageProjectionWorkerOptions
{
    public const string SectionName = "Promotion:UsageProjection";

    public required Uri BrokerUri { get; init; }

    public required string Exchange { get; init; }

    public string Queue { get; init; } = "promotion.analytics-usage";

    public string DeadLetterExchange { get; init; } = "aggregator.dead-letter";

    public string DeadLetterQueue { get; init; } = "promotion.analytics-usage.dead-letter";

    public string RoutingKey { get; init; } =
        AnalyticsPromotionUsageIntegrationContracts.RoutingKey;

    public ushort PrefetchCount { get; init; } = 8;

    public int DeliveryLimit { get; init; } = 8;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public static PromotionUsageProjectionWorkerOptions FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var brokerUriValue = Require(configuration, "Messaging:BrokerUri");
        if (!Uri.TryCreate(brokerUriValue, UriKind.Absolute, out var brokerUri))
        {
            throw new InvalidOperationException(
                "Messaging:BrokerUri must be an absolute URI.");
        }

        var options = new PromotionUsageProjectionWorkerOptions
        {
            BrokerUri = brokerUri,
            Exchange = Require(configuration, "Messaging:Exchange"),
            Queue = Read(configuration, $"{SectionName}:Queue", "promotion.analytics-usage"),
            DeadLetterExchange = Read(
                configuration,
                $"{SectionName}:DeadLetterExchange",
                "aggregator.dead-letter"),
            DeadLetterQueue = Read(
                configuration,
                $"{SectionName}:DeadLetterQueue",
                "promotion.analytics-usage.dead-letter"),
            RoutingKey = Read(
                configuration,
                $"{SectionName}:RoutingKey",
                AnalyticsPromotionUsageIntegrationContracts.RoutingKey),
            PrefetchCount = checked((ushort)ReadInt(
                configuration,
                $"{SectionName}:PrefetchCount",
                8)),
            DeliveryLimit = ReadInt(
                configuration,
                $"{SectionName}:DeliveryLimit",
                8),
            RetryDelay = TimeSpan.FromMilliseconds(ReadInt(
                configuration,
                $"{SectionName}:RetryDelayMilliseconds",
                500)),
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(BrokerUri);
        if (BrokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidOperationException(
                $"{SectionName}:BrokerUri must use amqp or amqps.");
        }

        RequireText(Exchange, nameof(Exchange));
        RequireText(Queue, nameof(Queue));
        RequireText(DeadLetterExchange, nameof(DeadLetterExchange));
        RequireText(DeadLetterQueue, nameof(DeadLetterQueue));
        RequireText(RoutingKey, nameof(RoutingKey));
        if (!string.Equals(
                RoutingKey,
                AnalyticsPromotionUsageIntegrationContracts.RoutingKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{SectionName}:RoutingKey must be the producer-owned Analytics Promotion usage key.");
        }

        if (PrefetchCount is < 1 or > 64)
        {
            throw new InvalidOperationException(
                $"{SectionName}:PrefetchCount must be between one and 64.");
        }

        if (DeliveryLimit is < 2 or > 100)
        {
            throw new InvalidOperationException(
                $"{SectionName}:DeliveryLimit must be between two and 100.");
        }

        if (RetryDelay < TimeSpan.FromMilliseconds(100) ||
            RetryDelay > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException(
                $"{SectionName}:RetryDelay must be between 100 milliseconds and one minute.");
        }
    }

    private static string Require(IConfiguration configuration, string path) =>
        configuration[path] is { Length: > 0 } value
            ? value.Trim()
            : throw new InvalidOperationException(
                $"Configuration value '{path}' is required.");

    private static string Read(
        IConfiguration configuration,
        string path,
        string defaultValue) =>
        configuration[path] is { Length: > 0 } value
            ? value.Trim()
            : defaultValue;

    private static int ReadInt(
        IConfiguration configuration,
        string path,
        int defaultValue)
    {
        var value = configuration[path];
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
            : throw new InvalidOperationException(
                $"Configuration value '{path}' must be an integer.");
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} must contain between one and 200 printable characters.");
        }
    }
}
