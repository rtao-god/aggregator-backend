using Aggregator.Query.Contracts;

namespace Aggregator.Analytics.Worker;

/// <summary>Validated RabbitMQ contract for the Analytics public-reference projection consumer.</summary>
public sealed record AnalyticsPublicReadProjectionWorkerOptions
{
    public const string SectionName = "Analytics:PublicReadProjection";

    public required Uri BrokerUri { get; init; }

    public string Exchange { get; init; } = "aggregator.events";

    public string Queue { get; init; } = "analytics.query-public-read-projection";

    public string DeadLetterExchange { get; init; } = "aggregator.dead-letter";

    public string DeadLetterQueue { get; init; } =
        "analytics.query-public-read-projection.dead-letter";

    public string RoutingKey { get; init; } =
        QueryIntegrationEventTypes.PublicReadRevisionActivated;

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

        RequireText(Exchange, nameof(Exchange), maximumLength: 200);
        RequireText(Queue, nameof(Queue), maximumLength: 200);
        RequireText(DeadLetterExchange, nameof(DeadLetterExchange), maximumLength: 200);
        RequireText(DeadLetterQueue, nameof(DeadLetterQueue), maximumLength: 200);
        RequireText(RoutingKey, nameof(RoutingKey), maximumLength: 200);
        if (!string.Equals(
                RoutingKey,
                QueryIntegrationEventTypes.PublicReadRevisionActivated,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{SectionName}:RoutingKey must be the producer-owned Query public-read activation key.");
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

    private static void RequireText(
        string value,
        string name,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} must contain between one and {maximumLength} characters.");
        }
    }
}
