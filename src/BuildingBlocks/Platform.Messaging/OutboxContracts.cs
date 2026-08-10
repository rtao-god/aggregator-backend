using Npgsql;

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
    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Reports that one failed dispatch attempt was durably recorded before control returned to the execution host.
/// </summary>
public sealed class OutboxDispatchAttemptException : InvalidOperationException
{
    /// <summary>Creates the exact recorded-attempt diagnostic.</summary>
    public OutboxDispatchAttemptException(
        Guid messageId,
        bool deadLettered,
        Exception innerException)
        : base(CreateMessage(messageId, deadLettered, innerException), innerException)
    {
        MessageId = messageId;
        DeadLettered = deadLettered;
    }

    /// <summary>The outbox message whose dispatch attempt failed.</summary>
    public Guid MessageId { get; }

    /// <summary>Whether the persisted delivery-attempt budget moved the message to dead letter.</summary>
    public bool DeadLettered { get; }

    private static string CreateMessage(
        Guid messageId,
        bool deadLettered,
        Exception innerException)
    {
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty outbox message ID is required.",
                nameof(messageId));
        }

        ArgumentNullException.ThrowIfNull(innerException);
        var state = deadLettered
            ? "was dead-lettered"
            : "remains eligible for another bounded attempt";
        return $"Outbox message '{messageId:D}' failed dispatch, the exact attempt was persisted, and the message {state}. Failure: {innerException.Message}";
    }
}

/// <summary>Classifies only execution-host failures that may safely return to the durable retry loop.</summary>
public static class OutboxDispatchFailurePolicy
{
    /// <summary>
    /// Returns true when durable attempt state is already recorded, another lease owns the transition,
    /// or the underlying PostgreSQL/I/O failure is explicitly transient.
    /// </summary>
    public static bool IsRecoverable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            OutboxDispatchAttemptException => true,
            OutboxLeaseLostException => true,
            NpgsqlException { IsTransient: true } => true,
            TimeoutException => true,
            IOException => true,
            AggregateException aggregateException =>
                aggregateException.InnerExceptions.Count > 0 &&
                aggregateException.InnerExceptions.All(IsRecoverable),
            _ when exception.InnerException is not null =>
                IsRecoverable(exception.InnerException),
            _ => false,
        };
    }
}

/// <summary>Reports that an outbox transition no longer owns the exact leased message.</summary>
public sealed class OutboxLeaseLostException : InvalidOperationException
{
    /// <summary>Creates an exact lost-lease diagnostic without mutating the replacement lease.</summary>
    public OutboxLeaseLostException(
        Guid messageId,
        Guid leaseToken,
        string dispatcherIdentity,
        Exception? innerException = null)
        : base(CreateMessage(messageId, leaseToken, dispatcherIdentity), innerException)
    {
        MessageId = messageId;
        LeaseToken = leaseToken;
        DispatcherIdentity = dispatcherIdentity;
    }

    /// <summary>The outbox message whose transition lost ownership.</summary>
    public Guid MessageId { get; }

    /// <summary>The exact lease token that no longer owns the message.</summary>
    public Guid LeaseToken { get; }

    /// <summary>The dispatcher that attempted the rejected transition.</summary>
    public string DispatcherIdentity { get; }

    private static string CreateMessage(
        Guid messageId,
        Guid leaseToken,
        string dispatcherIdentity)
    {
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty outbox message ID is required.", nameof(messageId));
        }

        if (leaseToken == Guid.Empty)
        {
            throw new ArgumentException("A non-empty outbox lease token is required.", nameof(leaseToken));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(dispatcherIdentity);
        return $"Outbox dispatcher '{dispatcherIdentity}' lost its exact lease '{leaseToken}' for message '{messageId}'; no completion or failure transition was written.";
    }
}

/// <summary>Owner-specific settings for leasing and dispatching a PostgreSQL outbox.</summary>
public sealed record OutboxDispatcherOptions
{
    public required string ConnectionString { get; init; }

    public required string Schema { get; init; }

    public required string DispatcherIdentity { get; init; }

    public int BatchSize { get; init; } = 50;

    public int MaximumDeliveryAttempts { get; init; } = 8;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan EmptyDelay { get; init; } = TimeSpan.FromSeconds(2);

    public void Validate()
    {
        RequireText(ConnectionString, nameof(ConnectionString));
        RequireSqlIdentifier(Schema, nameof(Schema));
        RequireText(DispatcherIdentity, nameof(DispatcherIdentity));
        if (BatchSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchSize), "BatchSize must be between 1 and 500.");
        }

        if (MaximumDeliveryAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumDeliveryAttempts),
                "MaximumDeliveryAttempts must be between 1 and 100.");
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
        if (!(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException("A safe unquoted PostgreSQL identifier is required.", parameterName);
        }
    }
}
