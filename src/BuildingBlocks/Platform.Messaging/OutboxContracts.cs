namespace Platform.Messaging;

/// <summary>A durable owner event awaiting broker delivery.</summary>
public sealed record OutboxMessage(
    Guid MessageId,
    string RoutingKey,
    string ContractIdentity,
    string PayloadJson,
    string PayloadDigest,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId);

/// <summary>Publishes one exact outbox message without changing its domain meaning.</summary>
public interface IIntegrationEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>Owner-specific settings for leasing and dispatching a PostgreSQL outbox.</summary>
public sealed record OutboxDispatcherOptions
{
    public required string ConnectionString { get; init; }

    public required string Schema { get; init; }

    public required string Exchange { get; init; }

    public required string DispatcherIdentity { get; init; }

    public int BatchSize { get; init; } = 50;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan EmptyDelay { get; init; } = TimeSpan.FromSeconds(2);

    public void Validate()
    {
        RequireText(ConnectionString, nameof(ConnectionString));
        RequireSqlIdentifier(Schema, nameof(Schema));
        RequireText(Exchange, nameof(Exchange));
        RequireText(DispatcherIdentity, nameof(DispatcherIdentity));
        if (BatchSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchSize), "BatchSize must be between 1 and 500.");
        }

        if (LeaseDuration <= TimeSpan.Zero || LeaseDuration > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration), "LeaseDuration must be positive and no longer than 15 minutes.");
        }

        if (EmptyDelay < TimeSpan.FromMilliseconds(100) || EmptyDelay > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(EmptyDelay), "EmptyDelay must be between 100 ms and one minute.");
        }
    }

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
    }

    private static void RequireSqlIdentifier(string value, string parameterName)
    {
        RequireText(value, parameterName);
        if (!(char.IsAsciiLetter(value[0]) || value[0] == '_') || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException("A safe unquoted PostgreSQL identifier is required.", parameterName);
        }
    }
}
