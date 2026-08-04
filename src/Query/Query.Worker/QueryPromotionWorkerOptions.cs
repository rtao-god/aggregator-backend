namespace Aggregator.Query.Worker;

public sealed record QueryPromotionWorkerOptions
{
    public const string SectionName = "Query:PromotionWorker";

    public required Uri BrokerUri { get; init; }

    public string Exchange { get; init; } = "aggregator.events";

    public string Queue { get; init; } = "query.promotion-overlay-projection";

    public string DeadLetterExchange { get; init; } = "aggregator.dead-letter";

    public string DeadLetterQueue { get; init; } = "query.promotion-overlay-projection.dead-letter";

    public string RoutingKey { get; init; } = "promotion.overlay.activated";

    public ushort PrefetchCount { get; init; } = 8;

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
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Query Promotion worker {name} is required.");
        }
    }
}
