namespace Aggregator.Promotion.Application;

internal static class PromotionOutboxMessageFactory
{
    public static PromotionOutboxMessage Create<TEvent>(
        Guid eventId,
        string eventType,
        string contractIdentity,
        TEvent integrationEvent,
        DateTimeOffset occurredAtUtc,
        PromotionCommandContext commandContext)
    {
        if (eventId == Guid.Empty)
        {
            throw new PromotionApplicationException(
                "Promotion.Messaging",
                "PROMOTION_EVENT_ID_INVALID",
                500,
                "Promotion event ID is empty.",
                "Correct the Promotion ID source before persisting the command.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractIdentity);
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentNullException.ThrowIfNull(commandContext);
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new PromotionApplicationException(
                "Promotion.Messaging",
                "PROMOTION_EVENT_TIME_NOT_UTC",
                500,
                "Promotion event time is not UTC.",
                "Correct the Promotion clock before persisting the command.");
        }

        var payload = PromotionCanonicalJson.SerializeToString(integrationEvent);
        var payloadDigest = PromotionCanonicalJson.ComputeDigest(integrationEvent);
        return new PromotionOutboxMessage(
            eventId,
            eventType.Trim(),
            contractIdentity.Trim(),
            payload,
            payloadDigest,
            occurredAtUtc,
            commandContext.CorrelationId,
            commandContext.CausationId);
    }
}
