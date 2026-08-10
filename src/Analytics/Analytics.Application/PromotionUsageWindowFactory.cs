using Aggregator.Analytics.Contracts;

namespace Aggregator.Analytics.Application;

/// <summary>Analytics-owned aggregate candidate for one exact sponsored-placement window.</summary>
public sealed record ClosedPromotionUsageWindow(
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
    long AggregateRevision);

/// <summary>Builds the producer-owned event only from an already closed Analytics aggregate.</summary>
public static class PromotionUsageWindowFactory
{
    public static PromotionUsageWindowClosed Create(
        ClosedPromotionUsageWindow window,
        Guid eventId,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(window);
        RequireIdentity(eventId, nameof(eventId));
        RequireIdentity(window.UsageWindowId, nameof(window.UsageWindowId));
        RequireIdentity(window.PlacementId, nameof(window.PlacementId));
        RequireIdentity(window.ListingId, nameof(window.ListingId));
        RequireIdentity(window.AggregationRunId, nameof(window.AggregationRunId));
        var catalogKey = RequireKey(window.CatalogKey, nameof(window.CatalogKey));
        var startsAtUtc = RequireUtc(window.WindowStartsAtUtc, nameof(window.WindowStartsAtUtc));
        var endsAtUtc = RequireUtc(window.WindowEndsAtUtc, nameof(window.WindowEndsAtUtc));
        var emittedAtUtc = RequireUtc(occurredAtUtc, nameof(occurredAtUtc));

        if (endsAtUtc <= startsAtUtc)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_WINDOW_INVALID",
                "Promotion usage window end must be after its start.");
        }

        if (endsAtUtc > emittedAtUtc)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_WINDOW_NOT_CLOSED",
                "Promotion usage cannot be emitted before the exact aggregate window is closed.");
        }

        if (window.AggregateRevision < 1)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_REVISION_INVALID",
                "Promotion usage aggregate revision must be positive.");
        }

        RequireCount(window.AcceptedImpressions, nameof(window.AcceptedImpressions));
        RequireCount(window.AcceptedListingOpens, nameof(window.AcceptedListingOpens));
        RequireCount(window.AcceptedOutboundClicks, nameof(window.AcceptedOutboundClicks));
        if (window.AcceptedImpressions == 0 &&
            window.AcceptedListingOpens == 0 &&
            window.AcceptedOutboundClicks == 0)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_EMPTY",
                "An empty usage window is not a publishable Promotion integration event.");
        }

        return new PromotionUsageWindowClosed(
            eventId,
            window.UsageWindowId,
            window.PlacementId,
            window.ListingId,
            catalogKey,
            startsAtUtc,
            endsAtUtc,
            window.AcceptedImpressions,
            window.AcceptedListingOpens,
            window.AcceptedOutboundClicks,
            window.AggregationRunId,
            window.AggregateRevision,
            emittedAtUtc);
    }

    private static void RequireIdentity(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_IDENTITY_INVALID",
                $"Promotion usage identity '{parameterName}' is required.");
        }
    }

    private static string RequireKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            value.Any(char.IsControl) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_KEY_INVALID",
                $"Promotion usage key '{parameterName}' is invalid.");
        }

        return value;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_TIME_NOT_UTC",
                $"Promotion usage timestamp '{parameterName}' must be UTC.");
        }

        return value;
    }

    private static void RequireCount(long value, string parameterName)
    {
        if (value < 0)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_COUNT_INVALID",
                $"Promotion usage count '{parameterName}' cannot be negative.");
        }
    }

    private static AnalyticsCommandException Failure(string code, string detail) =>
        new(
            "Analytics.PromotionUsage",
            code,
            422,
            detail,
            "Rebuild the exact closed Analytics window before publishing Promotion usage.");
}
