using Aggregator.Catalog.Contracts;

namespace Aggregator.Ingestion.Worker;

/// <summary>Validated RabbitMQ contract for the Ingestion Catalog configuration projection.</summary>
public sealed record IngestionCatalogConfigurationProjectionWorkerOptions
{
    public const string SectionName = "Ingestion:CatalogProjection";

    public required Uri BrokerUri { get; init; }

    public string Exchange { get; init; } = "aggregator.events";

    public string Queue { get; init; } = "ingestion.catalog-configuration-projection";

    public string DeadLetterExchange { get; init; } = "aggregator.dead-letter";

    public string DeadLetterQueue { get; init; } =
        "ingestion.catalog-configuration-projection.dead-letter";

    public string RoutingKey { get; init; } = CatalogIntegrationEventTypes.ConfigurationActivated;

    public ushort PrefetchCount { get; init; } = 8;

    public int DeliveryLimit { get; init; } = 8;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

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
                CatalogIntegrationEventTypes.ConfigurationActivated,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{SectionName}:RoutingKey must be the producer-owned Catalog configuration activation key.");
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

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} must contain between one and 200 printable characters.");
        }
    }
}
