namespace Aggregator.Promotion.Application;

/// <summary>Consumer-neutral Analytics usage envelope after worker-level wire validation.</summary>
public sealed record AnalyticsPromotionUsageProjectionMessage(
    Guid MessageId,
    string ContractIdentity,
    string PayloadDigest,
    string CorrelationId,
    string? CausationId,
    Guid EventId,
    Guid UsageWindowId,
    Guid PlacementId,
    Guid ListingId,
    string CatalogKey,
    DateTimeOffset WindowStartsAtUtc,
    DateTimeOffset WindowEndsAtUtc,
    long AcceptedImpressions,
    long AcceptedListingOpens,
    long AcceptedOutboundClicks,
    Guid AggregationRunId,
    long AggregateRevision,
    DateTimeOffset OccurredAtUtc);

/// <summary>Promotion-local immutable usage projection row derived from one Analytics event.</summary>
public sealed record PromotionUsageWindowProjection(
    Guid UsageWindowId,
    Guid PlacementId,
    Guid ListingId,
    string CatalogKey,
    DateTimeOffset WindowStartsAtUtc,
    DateTimeOffset WindowEndsAtUtc,
    long AcceptedImpressions,
    long AcceptedListingOpens,
    long AcceptedOutboundClicks,
    Guid AggregationRunId,
    long SourceAggregateRevision,
    Guid SourceMessageId,
    string SourcePayloadDigest,
    DateTimeOffset SourceOccurredAtUtc);

public enum PromotionUsageProjectionDisposition
{
    Applied = 1,
    Duplicate = 2,
}

public sealed record PromotionUsageProjectionResult(
    PromotionUsageWindowProjection Projection,
    PromotionUsageProjectionDisposition Disposition);

/// <summary>Atomic Promotion inbox and usage-projection persistence boundary.</summary>
public interface IPromotionUsageProjectionStore
{
    Task<PromotionUsageProjectionResult> ApplyAsync(
        PromotionUsageProjectionChange change,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken);
}

public sealed record PromotionUsageProjectionChange(
    Guid MessageId,
    string ContractIdentity,
    string PayloadDigest,
    string CorrelationId,
    string? CausationId,
    PromotionUsageWindowProjection Projection);

/// <summary>Applies one Analytics-owned closed usage window without re-evaluating traffic quality.</summary>
public sealed class ApplyAnalyticsPromotionUsageWindowService(
    IPromotionUsageProjectionStore store,
    TimeProvider timeProvider)
{
    public Task<PromotionUsageProjectionResult> ApplyAsync(
        AnalyticsPromotionUsageProjectionMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateEnvelope(message);
        var projection = ValidateProjection(message);
        return store.ApplyAsync(
            new PromotionUsageProjectionChange(
                message.MessageId,
                message.ContractIdentity,
                message.PayloadDigest,
                message.CorrelationId,
                message.CausationId,
                projection),
            RequireUtc(timeProvider.GetUtcNow(), nameof(TimeProvider)),
            cancellationToken);
    }

    private static void ValidateEnvelope(AnalyticsPromotionUsageProjectionMessage message)
    {
        RequireIdentity(message.MessageId, nameof(message.MessageId));
        if (message.EventId != message.MessageId)
        {
            throw Failure(
                "PROMOTION_USAGE_MESSAGE_ID_MISMATCH",
                "Broker message identity does not match the Analytics producer event identity.");
        }

        RequireText(message.ContractIdentity, nameof(message.ContractIdentity), 300);
        RequireDigest(message.PayloadDigest, nameof(message.PayloadDigest));
        RequireText(message.CorrelationId, nameof(message.CorrelationId), 200);
        if (message.CausationId is not null)
        {
            RequireText(message.CausationId, nameof(message.CausationId), 200);
        }
    }

    private static PromotionUsageWindowProjection ValidateProjection(
        AnalyticsPromotionUsageProjectionMessage message)
    {
        RequireIdentity(message.UsageWindowId, nameof(message.UsageWindowId));
        RequireIdentity(message.PlacementId, nameof(message.PlacementId));
        RequireIdentity(message.ListingId, nameof(message.ListingId));
        RequireIdentity(message.AggregationRunId, nameof(message.AggregationRunId));
        var catalogKey = RequireText(message.CatalogKey, nameof(message.CatalogKey), 200);
        var startsAtUtc = RequireUtc(message.WindowStartsAtUtc, nameof(message.WindowStartsAtUtc));
        var endsAtUtc = RequireUtc(message.WindowEndsAtUtc, nameof(message.WindowEndsAtUtc));
        var occurredAtUtc = RequireUtc(message.OccurredAtUtc, nameof(message.OccurredAtUtc));

        if (endsAtUtc <= startsAtUtc || endsAtUtc > occurredAtUtc)
        {
            throw Failure(
                "PROMOTION_USAGE_WINDOW_INVALID",
                "Analytics usage must describe an already closed positive UTC window.");
        }

        if (message.AggregateRevision < 1)
        {
            throw Failure(
                "PROMOTION_USAGE_REVISION_INVALID",
                "Analytics usage aggregate revision must be positive.");
        }

        RequireCount(message.AcceptedImpressions, nameof(message.AcceptedImpressions));
        RequireCount(message.AcceptedListingOpens, nameof(message.AcceptedListingOpens));
        RequireCount(message.AcceptedOutboundClicks, nameof(message.AcceptedOutboundClicks));
        return new PromotionUsageWindowProjection(
            message.UsageWindowId,
            message.PlacementId,
            message.ListingId,
            catalogKey,
            startsAtUtc,
            endsAtUtc,
            message.AcceptedImpressions,
            message.AcceptedListingOpens,
            message.AcceptedOutboundClicks,
            message.AggregationRunId,
            message.AggregateRevision,
            message.MessageId,
            message.PayloadDigest,
            occurredAtUtc);
    }

    private static void RequireIdentity(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw Failure(
                "PROMOTION_USAGE_IDENTITY_INVALID",
                $"Promotion usage identity '{parameterName}' is required.");
        }
    }

    private static string RequireText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Failure(
                "PROMOTION_USAGE_TEXT_INVALID",
                $"Promotion usage value '{parameterName}' is invalid.");
        }

        return value;
    }

    private static void RequireDigest(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw Failure(
                "PROMOTION_USAGE_DIGEST_INVALID",
                $"Promotion usage digest '{parameterName}' must be a SHA-256 hexadecimal value.");
        }
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "PROMOTION_USAGE_TIME_NOT_UTC",
                $"Promotion usage timestamp '{parameterName}' must be UTC.");
        }

        return value;
    }

    private static void RequireCount(long value, string parameterName)
    {
        if (value < 0)
        {
            throw Failure(
                "PROMOTION_USAGE_COUNT_INVALID",
                $"Promotion usage count '{parameterName}' cannot be negative.");
        }
    }

    private static PromotionApplicationException Failure(string code, string detail) =>
        new(
            "Promotion.Usage",
            code,
            422,
            detail,
            "Replay the exact Analytics-owned usage event after correcting its producer contract.");
}
