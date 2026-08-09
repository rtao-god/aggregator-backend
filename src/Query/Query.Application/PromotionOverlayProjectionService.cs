using Aggregator.Promotion.Contracts;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

public sealed record PromotionPlacementInboxMessage(
    Guid EventId,
    string PayloadDigest,
    DateTimeOffset ReceivedAtUtc)
{
    /// <summary>Correlation chain preserved from the producer message or started by a direct owner call.</summary>
    public string CorrelationId { get; init; } = EventId.ToString("D");
}

public enum PromotionPlacementProjectionDisposition
{
    Activated = 1,
    Replayed = 2,
    IgnoredStale = 3,
}

public sealed record PromotionPlacementProjectionResult(
    PublicReadRevision PublicReadRevision,
    PromotionPlacementProjectionDisposition Disposition)
{
    public bool Replayed => Disposition == PromotionPlacementProjectionDisposition.Replayed;

    public bool StaleIgnored => Disposition == PromotionPlacementProjectionDisposition.IgnoredStale;
}

public sealed record PromotionOverlayProjectionMaterialization(
    QueryOverlayRevision PromotionOverlay,
    PublicReadRevision PublicReadRevision,
    IReadOnlyList<QueryPromotionPlacement> Placements);

public interface IPromotionPlacementProjectionStore
{
    public Task<PromotionPlacementProjectionResult> ApplyAsync(
        QueryPromotionPlacement change,
        PromotionPlacementInboxMessage inboxMessage,
        CancellationToken cancellationToken);
}

public sealed class PromotionOverlayProjectionService(
    IPromotionPlacementProjectionStore store,
    IQueryClock clock)
{
    public Task<PromotionPlacementProjectionResult> ApplyAsync(
        SponsoredPlacementChanged change,
        string eventPayloadDigest,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            change,
            eventPayloadDigest,
            change?.EventId.ToString("D")
                ?? throw new ArgumentNullException(nameof(change)),
            cancellationToken);

    public async Task<PromotionPlacementProjectionResult> ApplyAsync(
        SponsoredPlacementChanged change,
        string eventPayloadDigest,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ValidateDigest(eventPayloadDigest, "event payload");
        var placement = Map(change);
        var inbox = new PromotionPlacementInboxMessage(
            change.EventId,
            eventPayloadDigest,
            clock.GetUtcNow())
        {
            CorrelationId = NormalizeCorrelationId(correlationId),
        };
        return await store.ApplyAsync(placement, inbox, cancellationToken);
    }

    private static QueryPromotionPlacement Map(SponsoredPlacementChanged change)
    {
        if (change.EventId == Guid.Empty)
        {
            throw Failure(
                "QUERY_PROMOTION_EVENT_ID_INVALID",
                "Promotion placement event ID is empty.",
                "Correct the Promotion outbox event before replaying it.");
        }

        return QueryPromotionPlacement.Create(
            change.PlacementId,
            change.EntitlementId,
            change.ListingId,
            change.CatalogKey,
            change.ProductKey,
            MapScope(change.ScopeType),
            change.ScopeKey,
            change.LocaleScope,
            change.StartsAtUtc,
            change.EndsAtUtc,
            change.HardExpiryAtUtc,
            change.PriorityBand,
            change.CapacitySlot,
            change.PresentationLabelKey,
            MapState(change.State),
            change.AggregateRevision,
            change.OccurredAtUtc);
    }

    private static QueryPromotionPlacementScope MapScope(PlacementScopeTypeContract value) => value switch
    {
        PlacementScopeTypeContract.Catalog => QueryPromotionPlacementScope.Catalog,
        PlacementScopeTypeContract.Category => QueryPromotionPlacementScope.Category,
        PlacementScopeTypeContract.District => QueryPromotionPlacementScope.District,
        PlacementScopeTypeContract.EditorialLanding => QueryPromotionPlacementScope.EditorialLanding,
        _ => throw Failure(
            "QUERY_PROMOTION_SCOPE_UNSUPPORTED",
            $"Promotion placement scope '{value}' is unsupported.",
            "Upgrade Query to the exact Promotion contract before replaying the event."),
    };

    private static QueryPromotionPlacementState MapState(SponsoredPlacementStateContract value) => value switch
    {
        SponsoredPlacementStateContract.Scheduled => QueryPromotionPlacementState.Scheduled,
        SponsoredPlacementStateContract.Active => QueryPromotionPlacementState.Active,
        SponsoredPlacementStateContract.Paused => QueryPromotionPlacementState.Paused,
        SponsoredPlacementStateContract.Ended => QueryPromotionPlacementState.Ended,
        SponsoredPlacementStateContract.Revoked => QueryPromotionPlacementState.Revoked,
        _ => throw Failure(
            "QUERY_PROMOTION_STATE_UNSUPPORTED",
            $"Promotion placement state '{value}' is unsupported.",
            "Upgrade Query to the exact Promotion contract before replaying the event."),
    };


    private static string NormalizeCorrelationId(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
        {
            throw Failure(
                "QUERY_PROMOTION_CORRELATION_INVALID",
                "Promotion message correlation ID is missing or too long.",
                "Republish the Promotion event with a bounded correlation identity.");
        }

        return correlationId.Trim();
    }

    private static void ValidateDigest(string digest, string owner)
    {
        if (string.IsNullOrWhiteSpace(digest) ||
            digest.Length != 64 ||
            digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw Failure(
                "QUERY_PROMOTION_DIGEST_INVALID",
                $"Promotion {owner} digest is invalid.",
                "Reject the message and inspect the Promotion outbox payload.");
        }
    }

    private static QueryProjectionException Failure(
        string code,
        string message,
        string requiredAction) =>
        new(
            "Query.PromotionProjection",
            code,
            422,
            message,
            requiredAction);
}

public static class PromotionOverlayProjectionBuilder
{
    public static PromotionOverlayProjectionMaterialization Build(
        PublicReadRevision currentPublicReadRevision,
        string baseProjectionDigest,
        string safetyOverlayDigest,
        long sourceRevision,
        IReadOnlyList<QueryPromotionPlacement> placements,
        Guid promotionOverlayId,
        Guid publicReadRevisionId,
        DateTimeOffset builtAtUtc)
    {
        ArgumentNullException.ThrowIfNull(currentPublicReadRevision);
        ArgumentNullException.ThrowIfNull(placements);
        if (sourceRevision <= 0)
        {
            throw Failure(
                "QUERY_PROMOTION_SOURCE_REVISION_INVALID",
                "Promotion overlay source revision must be positive.",
                "Correct Query projection revision allocation before persistence.");
        }

        var orderedPlacements = placements
            .Where(item => item.IsMaterialized)
            .OrderByDescending(item => item.PriorityBand)
            .ThenBy(item => item.CapacitySlot)
            .ThenBy(item => item.PlacementId)
            .ToArray();
        if (orderedPlacements.Select(item => item.PlacementId).Distinct().Count() != orderedPlacements.Length)
        {
            throw Failure(
                "QUERY_PROMOTION_PLACEMENT_DUPLICATE",
                "Promotion overlay contains a duplicate placement identity.",
                "Correct the Query placement-state projection before materialization.");
        }

        var builtAt = builtAtUtc.Offset == TimeSpan.Zero
            ? builtAtUtc
            : throw Failure(
                "QUERY_PROMOTION_BUILD_TIMESTAMP_NOT_UTC",
                "Promotion overlay build timestamp is not UTC.",
                "Normalize the Query owner clock to UTC before materialization.");
        var overlayDigest = QueryCanonicalJson.ComputeDigest(new
        {
            currentPublicReadRevision.CatalogKey,
            Kind = QueryOverlayKind.Promotion,
            SourceRevision = sourceRevision,
            Placements = orderedPlacements.Select(item => new
            {
                item.PlacementId,
                item.EntitlementId,
                item.ListingId,
                item.ProductKey,
                ScopeType = item.ScopeType.ToString(),
                item.ScopeKey,
                item.LocaleScope,
                item.StartsAtUtc,
                item.EndsAtUtc,
                item.HardExpiryAtUtc,
                item.PriorityBand,
                item.CapacitySlot,
                item.PresentationLabelKey,
                item.AggregateRevision,
            }).ToArray(),
        });
        var overlay = QueryOverlayRevision.Create(
            promotionOverlayId,
            currentPublicReadRevision.CatalogKey,
            QueryOverlayKind.Promotion,
            sourceRevision,
            builtAt,
            overlayDigest,
            orderedPlacements.Length);
        var publicReadDigest = QueryCanonicalJson.ComputeDigest(new
        {
            currentPublicReadRevision.CatalogKey,
            currentPublicReadRevision.BaseProjectionId,
            PromotionOverlayId = overlay.Id,
            currentPublicReadRevision.SafetyOverlayId,
            currentPublicReadRevision.SourcePublicationId,
            BaseProjectionDigest = baseProjectionDigest,
            PromotionOverlayDigest = overlay.ContentDigest,
            SafetyOverlayDigest = safetyOverlayDigest,
        });
        var publicReadRevision = PublicReadRevision.Restore(
            publicReadRevisionId,
            currentPublicReadRevision.CatalogKey,
            currentPublicReadRevision.BaseProjectionId,
            overlay.Id,
            currentPublicReadRevision.SafetyOverlayId,
            currentPublicReadRevision.SourcePublicationId,
            builtAt,
            publicReadDigest);
        return new PromotionOverlayProjectionMaterialization(
            overlay,
            publicReadRevision,
            Array.AsReadOnly(orderedPlacements));
    }

    private static QueryProjectionException Failure(
        string code,
        string message,
        string requiredAction) =>
        new(
            "Query.PromotionProjection",
            code,
            500,
            message,
            requiredAction);
}
