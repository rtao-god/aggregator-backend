namespace Aggregator.Promotion.Overlay.Worker;

public sealed record PromotionOverlayWorkerOptions
{
    public const string SectionName = "Promotion:OverlayWorker";

    public required Uri BrokerUri { get; init; }

    public string Exchange { get; init; } = "aggregator.events";

    public string WorkerId { get; init; } = "promotion-overlay-outbox";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    public int MaximumAttempts { get; init; } = 10;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(BrokerUri);
        if (BrokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidOperationException("Promotion overlay broker URI must use amqp or amqps.");
        }

        if (string.IsNullOrWhiteSpace(Exchange) || string.IsNullOrWhiteSpace(WorkerId))
        {
            throw new InvalidOperationException("Promotion overlay exchange and worker ID are required.");
        }

        if (PollInterval < TimeSpan.FromMilliseconds(100) ||
            PollInterval > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("Promotion overlay poll interval must be between 100 ms and one minute.");
        }

        if (LeaseDuration < TimeSpan.FromSeconds(5) ||
            LeaseDuration > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("Promotion overlay lease duration must be between five seconds and five minutes.");
        }

        if (MaximumAttempts is < 1 or > 100)
        {
            throw new InvalidOperationException("Promotion overlay maximum attempts must be between one and 100.");
        }
    }
}
