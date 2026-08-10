using System.Text;
using Aggregator.Analytics.Contracts;

namespace Aggregator.Analytics.Application;

/// <summary>Exact durable outbox message produced from one Analytics-owned usage revision.</summary>
public sealed record AnalyticsPromotionUsageOutboxMessage(
    Guid MessageId,
    string RoutingKey,
    string ContractIdentity,
    string PayloadJson,
    string PayloadDigest,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid CausationId);

/// <summary>Owns canonical serialization and envelope identity for Promotion usage events.</summary>
public static class PromotionUsageOutboxMessageFactory
{
    public static AnalyticsPromotionUsageOutboxMessage Create(
        ClosedPromotionUsageWindow window,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        string correlationId,
        Guid causationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) ||
            correlationId.Length > 128 ||
            correlationId.Any(char.IsControl) ||
            !string.Equals(correlationId, correlationId.Trim(), StringComparison.Ordinal))
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_CORRELATION_INVALID",
                "Promotion usage outbox correlation identity is absent or invalid.");
        }

        if (causationId == Guid.Empty)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_CAUSATION_INVALID",
                "Promotion usage outbox causation identity is required.");
        }

        var integrationEvent = PromotionUsageWindowFactory.Create(
            window,
            eventId,
            occurredAtUtc);
        var payload = AnalyticsCanonicalJson.Serialize(integrationEvent);
        return new AnalyticsPromotionUsageOutboxMessage(
            integrationEvent.EventId,
            AnalyticsPromotionUsageIntegrationContracts.RoutingKey,
            AnalyticsPromotionUsageIntegrationContracts.ContractIdentity,
            Encoding.UTF8.GetString(payload),
            AnalyticsCanonicalJson.ComputeDigest(payload),
            integrationEvent.OccurredAtUtc,
            correlationId,
            causationId);
    }

    private static AnalyticsCommandException Failure(string code, string detail) =>
        new(
            "Analytics.PromotionUsage",
            code,
            422,
            detail,
            "Rebuild the exact Analytics usage revision with valid correlation and causation identity.");
}
