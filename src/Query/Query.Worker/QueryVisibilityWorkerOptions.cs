using Aggregator.Catalog.Contracts;

namespace Aggregator.Query.Worker;

public sealed record QueryVisibilityWorkerOptions
{
    public const string SectionName = "Query:VisibilityWorker";

    public required Uri BrokerUri { get; init; }

    public string Exchange { get; init; } = "aggregator.events";

    public string Queue { get; init; } = "query.catalog-visibility-safety";

    public string DeadLetterExchange { get; init; } = "aggregator.dead-letter";

    public string DeadLetterQueue { get; init; } = "query.catalog-visibility-safety.dead-letter";

    public string RoutingKey { get; init; } =
        CatalogIntegrationEventTypes.PublicVisibilitySuppressionChanged;

    public ushort PrefetchCount { get; init; } = 4;

    public int DeliveryLimit { get; init; } = 8;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(BrokerUri);
        if (BrokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidOperationException(
                "Query visibility worker broker URI must use amqp or amqps.");
        }

        RequireText(Exchange, nameof(Exchange));
        RequireText(Queue, nameof(Queue));
        RequireText(DeadLetterExchange, nameof(DeadLetterExchange));
        RequireText(DeadLetterQueue, nameof(DeadLetterQueue));
        RequireText(RoutingKey, nameof(RoutingKey));
        if (PrefetchCount is < 1 or > 64)
        {
            throw new InvalidOperationException(
                "Query visibility worker prefetch count must be between one and 64.");
        }

        if (DeliveryLimit is < 2 or > 100)
        {
            throw new InvalidOperationException(
                "Query visibility worker delivery limit must be between two and 100.");
        }

        if (RetryDelay < TimeSpan.FromMilliseconds(100) ||
            RetryDelay > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException(
                "Query visibility worker retry delay must be between 100 milliseconds and one minute.");
        }
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Query visibility worker {name} is required.");
        }
    }
}
