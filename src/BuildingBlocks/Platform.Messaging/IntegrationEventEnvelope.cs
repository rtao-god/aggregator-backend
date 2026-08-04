using System.Text.Json;

namespace Platform.Messaging;

/// <summary>Producer-owned metadata carried by every asynchronous integration event.</summary>
public sealed record IntegrationEventEnvelope(
    Guid MessageId,
    string EventName,
    string ContractRevision,
    string Producer,
    Guid AggregateId,
    long AggregateRevision,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId,
    string PayloadDigest,
    JsonElement Payload)
{
    public IntegrationEventEnvelope Validate()
    {
        if (MessageId == Guid.Empty)
        {
            throw new ArgumentException("MessageId is required.", nameof(MessageId));
        }

        RequireText(EventName, nameof(EventName));
        RequireText(ContractRevision, nameof(ContractRevision));
        RequireText(Producer, nameof(Producer));
        if (AggregateId == Guid.Empty)
        {
            throw new ArgumentException("AggregateId is required.", nameof(AggregateId));
        }

        if (AggregateRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AggregateRevision), "AggregateRevision must be positive.");
        }

        if (OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("OccurredAtUtc must be normalized to UTC.", nameof(OccurredAtUtc));
        }

        RequireText(CorrelationId, nameof(CorrelationId));
        if (PayloadDigest.Length != 64 || !PayloadDigest.All(IsLowerHex))
        {
            throw new ArgumentException("PayloadDigest must be a lowercase SHA-256 hex digest.", nameof(PayloadDigest));
        }

        if (Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("Payload is required.", nameof(Payload));
        }

        return this;
    }

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
    }
}
