using Aggregator.Catalog.Contracts;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

public enum VisibilitySafetyProjectionDisposition
{
    Activated = 1,
    Replayed = 2,
    IgnoredStale = 3,
}

public sealed record VisibilitySuppressionInboxMessage(
    Guid EventId,
    string PayloadDigest,
    DateTimeOffset ReceivedAtUtc);

public sealed record VisibilitySafetyProjectionMaterialization(
    QueryOverlayRevision Overlay,
    PublicReadRevision PublicReadRevision,
    IReadOnlyList<QueryVisibilitySuppression> ActiveSuppressions);

public sealed record VisibilitySafetyProjectionResult(
    PublicReadRevision PublicReadRevision,
    VisibilitySafetyProjectionDisposition Disposition);

/// <summary>
/// Persists Catalog safety events through a durable block-first protocol and atomically switches
/// the resulting immutable safety overlay and composite public-read revision.
/// </summary>
public interface IVisibilitySafetyProjectionStore
{
    public Task<VisibilitySafetyProjectionResult> ApplyAsync(
        QueryVisibilitySuppression suppression,
        VisibilitySuppressionInboxMessage inboxMessage,
        CancellationToken cancellationToken);
}

/// <summary>Maps the producer-owned Catalog event and invokes the Query safety owner.</summary>
public sealed class VisibilitySafetyProjectionService(
    IVisibilitySafetyProjectionStore store,
    IQueryClock clock)
{
    public Task<VisibilitySafetyProjectionResult> ApplyAsync(
        CatalogPublicVisibilitySuppressionChanged change,
        string payloadDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDigest);
        var suppression = ToDomain(change);
        var inbox = new VisibilitySuppressionInboxMessage(
            change.EventId,
            payloadDigest,
            RequireUtc(clock.GetUtcNow(), "Query visibility inbox clock"));
        return store.ApplyAsync(suppression, inbox, cancellationToken);
    }

    private static QueryVisibilitySuppression ToDomain(
        CatalogPublicVisibilitySuppressionChanged change)
    {
        if (change.State == PublicVisibilitySuppressionStateContract.Requested)
        {
            throw Failure(
                "QUERY_VISIBILITY_REQUESTED_EVENT_FORBIDDEN",
                422,
                "Catalog emitted private requested suppression state to Query.",
                "Publish only active or resolved suppression revisions from Catalog.");
        }

        return QueryVisibilitySuppression.Create(
            change.SuppressionId,
            change.CatalogKey,
            change.Target.Kind switch
            {
                PublicVisibilitySuppressionTargetKindContract.Listing =>
                    QueryVisibilitySuppressionTargetKind.Listing,
                PublicVisibilitySuppressionTargetKindContract.Media =>
                    QueryVisibilitySuppressionTargetKind.Media,
                PublicVisibilitySuppressionTargetKindContract.Contact =>
                    QueryVisibilitySuppressionTargetKind.Contact,
                PublicVisibilitySuppressionTargetKindContract.Route =>
                    QueryVisibilitySuppressionTargetKind.Route,
                PublicVisibilitySuppressionTargetKindContract.ExternalReference =>
                    QueryVisibilitySuppressionTargetKind.ExternalReference,
                _ => throw Failure(
                    "QUERY_VISIBILITY_TARGET_KIND_UNSUPPORTED",
                    422,
                    $"Catalog visibility target kind '{change.Target.Kind}' is unsupported.",
                    "Republish the event with a supported Catalog visibility target kind."),
            },
            change.Target.ListingId,
            change.Target.TargetKey,
            change.PublicReasonClass,
            change.ResponseMode switch
            {
                PublicVisibilitySuppressionResponseModeContract.HideAsNotFound =>
                    QueryVisibilitySuppressionResponseMode.HideAsNotFound,
                PublicVisibilitySuppressionResponseModeContract.Gone =>
                    QueryVisibilitySuppressionResponseMode.Gone,
                PublicVisibilitySuppressionResponseModeContract.TemporarilyUnavailable =>
                    QueryVisibilitySuppressionResponseMode.TemporarilyUnavailable,
                PublicVisibilitySuppressionResponseModeContract.OmitChildElement =>
                    QueryVisibilitySuppressionResponseMode.OmitChildElement,
                _ => throw Failure(
                    "QUERY_VISIBILITY_RESPONSE_MODE_UNSUPPORTED",
                    422,
                    $"Catalog visibility response mode '{change.ResponseMode}' is unsupported.",
                    "Republish the event with a supported Catalog visibility response mode."),
            },
            change.State switch
            {
                PublicVisibilitySuppressionStateContract.Active =>
                    QueryVisibilitySuppressionState.Active,
                PublicVisibilitySuppressionStateContract.Resolved =>
                    QueryVisibilitySuppressionState.Resolved,
                _ => throw Failure(
                    "QUERY_VISIBILITY_STATE_UNSUPPORTED",
                    422,
                    $"Catalog visibility suppression state '{change.State}' is unsupported.",
                    "Publish only active or resolved suppression revisions from Catalog."),
            },
            change.StartsAtUtc,
            change.ExpiresAtUtc,
            change.AggregateRevision,
            change.OccurredAtUtc);
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string owner)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "QUERY_VISIBILITY_CLOCK_NOT_UTC",
                500,
                $"{owner} returned a non-UTC timestamp.",
                "Configure the Query worker clock to return UTC timestamps.");
        }

        return value;
    }

    private static QueryProjectionException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction) =>
        new(
            "Query.VisibilitySafety",
            code,
            statusCode,
            message,
            requiredAction);
}

public static class VisibilitySafetyProjectionBuilder
{
    public static VisibilitySafetyProjectionMaterialization Build(
        PublicReadRevision currentRevision,
        string baseProjectionDigest,
        string promotionOverlayDigest,
        long nextSafetySourceRevision,
        IEnumerable<QueryVisibilitySuppression> activeSuppressions,
        Guid overlayId,
        Guid publicReadRevisionId,
        DateTimeOffset builtAtUtc)
    {
        ArgumentNullException.ThrowIfNull(currentRevision);
        ArgumentNullException.ThrowIfNull(activeSuppressions);
        if (nextSafetySourceRevision <= 0)
        {
            throw Failure(
                "QUERY_VISIBILITY_SOURCE_REVISION_INVALID",
                "Visibility safety overlay source revision must be positive.");
        }

        var suppressions = activeSuppressions
            .OrderBy(item => item.SuppressionId)
            .ToArray();
        if (suppressions.Any(item =>
                item.State != QueryVisibilitySuppressionState.Active ||
                !string.Equals(item.CatalogKey, currentRevision.CatalogKey, StringComparison.Ordinal)))
        {
            throw Failure(
                "QUERY_VISIBILITY_ACTIVE_SET_INVALID",
                "Visibility safety overlay input contains a non-active or foreign-catalog suppression.");
        }

        var overlayDigest = QueryCanonicalJson.ComputeDigest(new
        {
            currentRevision.CatalogKey,
            kind = QueryOverlayKind.VisibilitySafety,
            sourceRevision = nextSafetySourceRevision,
            items = suppressions.Select(ToDigestItem).ToArray(),
        });
        var overlay = QueryOverlayRevision.Create(
            overlayId,
            currentRevision.CatalogKey,
            QueryOverlayKind.VisibilitySafety,
            nextSafetySourceRevision,
            builtAtUtc,
            overlayDigest,
            suppressions.Length);
        var publicReadDigest = QueryCanonicalJson.ComputeDigest(new
        {
            baseProjectionDigest,
            promotionOverlayDigest,
            safetyOverlayDigest = overlay.ContentDigest,
            currentRevision.SourcePublicationId,
        });
        var publicReadRevision = PublicReadRevision.Restore(
            publicReadRevisionId,
            currentRevision.CatalogKey,
            currentRevision.BaseProjectionId,
            currentRevision.PromotionOverlayId,
            overlay.Id,
            currentRevision.SourcePublicationId,
            builtAtUtc,
            publicReadDigest);
        return new VisibilitySafetyProjectionMaterialization(
            overlay,
            publicReadRevision,
            Array.AsReadOnly(suppressions));
    }

    private static object ToDigestItem(QueryVisibilitySuppression item) => new
    {
        item.SuppressionId,
        item.TargetKind,
        item.ListingId,
        item.TargetKey,
        item.PublicReasonClass,
        item.ResponseMode,
        item.StartsAtUtc,
        item.ExpiresAtUtc,
        item.AggregateRevision,
        item.OccurredAtUtc,
    };

    private static QueryProjectionException Failure(string code, string message) =>
        new(
            "Query.VisibilitySafety",
            code,
            500,
            message,
            "Correct the Query visibility safety projection input before activation.");
}
