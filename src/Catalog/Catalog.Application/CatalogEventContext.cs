using System.Text;

namespace Aggregator.Catalog.Application;

/// <summary>Correlation and causation metadata propagated into one Catalog integration event.</summary>
public sealed record CatalogEventContext
{
    private CatalogEventContext(string correlationId, Guid? causationId)
    {
        CorrelationId = correlationId;
        CausationId = causationId;
    }

    public string CorrelationId { get; }

    public Guid? CausationId { get; }

    public static CatalogEventContext Create(string correlationId, Guid? causationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var normalized = correlationId.Trim();
        if (normalized.Length is < 8 or > 128 || normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            throw new CatalogContractException(
                "catalog.correlation_id_invalid",
                "Catalog correlation ID must contain 8 to 128 allowlisted ASCII characters.");
        }

        if (causationId == Guid.Empty)
        {
            throw new CatalogContractException(
                "catalog.causation_id_invalid",
                "Catalog causation ID must be absent or a non-empty UUID.");
        }

        return new CatalogEventContext(normalized, causationId);
    }

    /// <summary>Starts a new explicit correlation root for a direct application or operator command.</summary>
    public static CatalogEventContext StartRoot() =>
        Create(Guid.CreateVersion7().ToString("D"));
}

/// <summary>Builds one exact producer event write from canonical payload bytes.</summary>
public static class CatalogOutboxMessageFactory
{
    public static CatalogOutboxMessage Create<T>(
        Guid messageId,
        string eventType,
        string contractIdentity,
        T payload,
        DateTimeOffset occurredAtUtc,
        CatalogEventContext context)
    {
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("Event message ID is required.", nameof(messageId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractIdentity);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new CatalogContractException(
                "catalog.event_timestamp_not_utc",
                "Catalog event timestamp must be normalized to UTC.");
        }

        var payloadJson = CatalogCanonicalJson.SerializeEvent(payload);
        var payloadDigest = CatalogCanonicalJson.ComputeSha256(Encoding.UTF8.GetBytes(payloadJson));
        return new CatalogOutboxMessage(
            messageId,
            eventType.Trim(),
            contractIdentity.Trim(),
            payloadJson,
            payloadDigest,
            occurredAtUtc,
            context.CorrelationId,
            context.CausationId);
    }
}
