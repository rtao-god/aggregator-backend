namespace Aggregator.Query.Worker;

public sealed record QueryWorkerOptions
{
    public const string SectionName = "Query:Worker";

    public required Uri BrokerUri { get; init; }

    public string Exchange { get; init; } = "aggregator.events";

    public string Queue { get; init; } = "query.catalog-publication-projection";

    public string RoutingKey { get; init; } = "catalog.publication.activated";

    public ushort PrefetchCount { get; init; } = 8;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(BrokerUri);
        if (BrokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidOperationException("Query worker broker URI must use amqp or amqps.");
        }

        RequireText(Exchange, nameof(Exchange));
        RequireText(Queue, nameof(Queue));
        RequireText(RoutingKey, nameof(RoutingKey));
        if (PrefetchCount is < 1 or > 256)
        {
            throw new InvalidOperationException("Query worker prefetch count must be between 1 and 256.");
        }
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Query worker {name} is required.");
        }
    }
}
