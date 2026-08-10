using Platform.Messaging;

namespace Aggregator.Analytics.Worker;

/// <summary>Validated runtime contract for Analytics-owned Promotion usage event delivery.</summary>
public sealed record AnalyticsOutboxWorkerOptions
{
    public const string SectionName = "Analytics:Outbox";

    public required string ConnectionString { get; init; }

    public required Uri BrokerUri { get; init; }

    public string Exchange { get; init; } = "aggregator.events";

    public string DispatcherIdentity { get; init; } = "analytics-promotion-usage-outbox";

    public int BatchSize { get; init; } = 50;

    public int MaximumDeliveryAttempts { get; init; } = 8;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan EmptyDelay { get; init; } = TimeSpan.FromSeconds(2);

    public void Validate()
    {
        CreateDispatcherOptions().Validate();
        CreatePublisherOptions().Validate();
    }

    public OutboxDispatcherOptions CreateDispatcherOptions() =>
        new()
        {
            ConnectionString = ConnectionString,
            Schema = "messaging",
            DispatcherIdentity = DispatcherIdentity,
            BatchSize = BatchSize,
            MaximumDeliveryAttempts = MaximumDeliveryAttempts,
            LeaseDuration = LeaseDuration,
            EmptyDelay = EmptyDelay,
        };

    public RabbitMqPublisherOptions CreatePublisherOptions() =>
        new()
        {
            BrokerUri = BrokerUri,
            Exchange = Exchange,
            ClientProvidedName = DispatcherIdentity,
        };
}
