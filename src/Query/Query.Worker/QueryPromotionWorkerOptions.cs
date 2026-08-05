using Aggregator.Promotion.Contracts;

namespace Aggregator.Query.Worker;

public sealed record QueryPromotionWorkerOptions
{
    public const string SectionName = "Query:PromotionWorker";

    public required Uri BrokerUri { get; init; }

    public string Exchange { get; init; } = "aggregator.events";

    public string Queue { get; init; } = "query.promotion-placement-projection";

    public string DeadLetterExchange { get; init; } = "aggregator.dead-letter";

    public string DeadLetterQueue { get; init; } = "query.promotion-placement-projection.dead-letter";

    public string RoutingKey { get; init; } = PromotionIntegrationEventTypes.PlacementChanged;

    public ushort PrefetchCount { get; init; } = 8;

    public int DeliveryLimit { get; init; } = 8;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(BrokerUri);
        if (BrokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidOperationException("Query Promotion worker broker URI must use amqp or amqps.");
        }

        RequireText(Exchange, nameof(Exchange));
        RequireText(Queue, nameof(Queue));
        RequireText(DeadLetterExchange, nameof(DeadLetterExchange));
        RequireText(DeadLetterQueue, nameof(DeadLetterQueue));
        RequireText(RoutingKey, nameof(RoutingKey));
        if (PrefetchCount is < 1 or > 256)
        {
            throw new InvalidOperationException(
                "Query Promotion worker prefetch count must be between one and 256.");
        }

        if (DeliveryLimit is < 2 or > 100)
        {
            throw new InvalidOperationException(
                "Query Promotion worker delivery limit must be between two and 100.");
        }

        if (RetryDelay < TimeSpan.FromMilliseconds(100) ||
            RetryDelay > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException(
                "Query Promotion worker retry delay must be between 100 milliseconds and one minute.");
        }
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Query Promotion worker {name} is required.");
        }
    }
}
