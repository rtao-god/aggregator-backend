using Aggregator.Promotion.Domain;

namespace Aggregator.Promotion.Application;

/// <summary>
/// Applies fail-closed eligibility and entitlement gates before a scheduled placement can become active.
/// </summary>
public static class PromotionScheduledPlacementPolicy
{
    public static bool Synchronize(
        SponsoredPlacement placement,
        PromotionEntitlement entitlement,
        PromotionProduct product,
        ListingPromotionEligibility? eligibility,
        Guid actorId,
        DateTimeOffset changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(entitlement);
        ArgumentNullException.ThrowIfNull(product);
        _ = PromotionActor.Create(actorId);
        if (changedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "PROMOTION_SCHEDULE_TIMESTAMP_NOT_UTC",
                "Promotion placement schedule timestamp must be normalized to UTC.",
                "Correct the Promotion worker clock before processing scheduled state.");
        }

        if (entitlement.Id != placement.EntitlementId ||
            entitlement.ListingId != placement.ListingId ||
            !string.Equals(
                entitlement.ProductKey,
                placement.ProductKey,
                StringComparison.Ordinal))
        {
            throw Failure(
                "PROMOTION_SCHEDULE_ENTITLEMENT_MISMATCH",
                $"Placement '{placement.Id}' is not bound to entitlement '{entitlement.Id}'.",
                "Restore the exact Promotion entitlement before resuming scheduled transitions.");
        }

        if (!string.Equals(product.Key, placement.ProductKey, StringComparison.Ordinal))
        {
            throw Failure(
                "PROMOTION_SCHEDULE_PRODUCT_MISMATCH",
                $"Placement '{placement.Id}' is not bound to product '{product.Key}'.",
                "Restore the exact Promotion product before resuming scheduled transitions.");
        }

        if (placement.State is
            SponsoredPlacementState.Paused or
            SponsoredPlacementState.Ended or
            SponsoredPlacementState.Revoked)
        {
            return false;
        }

        if (changedAtUtc < placement.CurrentRevision.EffectiveWindow.StartsAtUtc ||
            changedAtUtc >= placement.HardExpiryAtUtc)
        {
            return placement.SynchronizeTime(
                placement.AggregateRevision,
                changedAtUtc);
        }

        if (!entitlement.IsEffectiveAt(changedAtUtc))
        {
            placement.Pause(
                placement.AggregateRevision,
                actorId,
                $"entitlement revision {entitlement.AggregateRevision} is not effective at scheduled activation",
                changedAtUtc);
            return true;
        }

        if (eligibility is null)
        {
            placement.Pause(
                placement.AggregateRevision,
                actorId,
                "catalog eligibility projection is unavailable at scheduled activation",
                changedAtUtc);
            return true;
        }

        if (placement.PauseWhenCatalogIneligible(
                eligibility,
                product,
                actorId,
                changedAtUtc))
        {
            return true;
        }

        return placement.SynchronizeTime(
            placement.AggregateRevision,
            changedAtUtc);
    }

    private static PromotionApplicationException Failure(
        string code,
        string detail,
        string requiredAction) =>
        new(
            "Promotion.Scheduling",
            code,
            500,
            detail,
            requiredAction);
}
