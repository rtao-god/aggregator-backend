using Aggregator.Promotion.Application;
using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Promotion.Infrastructure;

public sealed partial class EfPromotionRepository
{
    /// <summary>
    /// Advances due entitlement and placement clocks as independent owner transactions. A corrupt later
    /// schedule item cannot roll back already committed transitions or their matching outbox messages.
    /// </summary>
    public async Task<int> SynchronizeDueAsync(
        DateTimeOffset nowUtc,
        Guid systemActorId,
        int batchSize,
        IPromotionIdSource idSource,
        CancellationToken cancellationToken)
    {
        ValidateSchedulerInput(nowUtc, systemActorId, batchSize, idSource);
        var commandContext = PromotionCommandContext.Start(
            PromotionActor.Create(systemActorId),
            $"promotion-scheduler:{nowUtc:yyyyMMddHHmmssfffffff}");
        var changed = 0;

        var entitlementIds = await _dbContext.Entitlements
            .AsNoTracking()
            .Where(row =>
                row.State == (int)PromotionEntitlementState.Scheduled &&
                row.StartsAtUtc <= nowUtc ||
                row.State == (int)PromotionEntitlementState.Active &&
                row.EndsAtUtc <= nowUtc)
            .OrderBy(row => row.EndsAtUtc)
            .ThenBy(row => row.StartsAtUtc)
            .ThenBy(row => row.Id)
            .Select(row => row.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);
        foreach (var entitlementId in entitlementIds)
        {
            changed += await SynchronizeEntitlementAsync(
                entitlementId,
                nowUtc,
                commandContext,
                idSource,
                cancellationToken);
            if (changed == batchSize)
            {
                return changed;
            }
        }

        _dbContext.ChangeTracker.Clear();
        var remaining = batchSize - changed;
        var placementCandidates = await (
                from placement in _dbContext.Placements.AsNoTracking()
                join revision in _dbContext.PlacementRevisions.AsNoTracking()
                    on placement.CurrentRevisionId equals revision.Id
                where
                    (placement.State == (int)SponsoredPlacementState.Scheduled ||
                     placement.State == (int)SponsoredPlacementState.Active) &&
                    (
                        placement.State == (int)SponsoredPlacementState.Scheduled &&
                        revision.StartsAtUtc <= nowUtc ||
                        revision.EndsAtUtc <= nowUtc ||
                        !_dbContext.Entitlements.Any(entitlement =>
                            entitlement.Id == placement.EntitlementId &&
                            entitlement.State == (int)PromotionEntitlementState.Active &&
                            entitlement.StartsAtUtc <= nowUtc &&
                            entitlement.EndsAtUtc > nowUtc) ||
                        !_dbContext.Products.Any(product =>
                            product.ProductKey == placement.ProductKey &&
                            product.State == (int)PromotionProductState.Active)
                    )
                orderby revision.EndsAtUtc, revision.StartsAtUtc, placement.Id
                select new PlacementScheduleCandidate(
                    placement.Id,
                    placement.ListingId,
                    revision.CatalogKey))
            .Take(remaining)
            .ToArrayAsync(cancellationToken);
        foreach (var candidate in placementCandidates)
        {
            changed += await SynchronizePlacementAsync(
                candidate,
                nowUtc,
                systemActorId,
                commandContext,
                idSource,
                cancellationToken);
        }

        return changed;
    }

    private async Task<int> SynchronizeEntitlementAsync(
        Guid entitlementId,
        DateTimeOffset nowUtc,
        PromotionCommandContext commandContext,
        IPromotionIdSource idSource,
        CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();
        try
        {
            return await ExecuteInTransactionAsync(async innerCancellationToken =>
            {
                var row = await _dbContext.Entitlements
                    .FromSqlInterpolated($$"""
                        SELECT *
                        FROM entitlements.promotion_entitlement
                        WHERE id = {{entitlementId}}
                        FOR UPDATE
                        """)
                    .SingleOrDefaultAsync(innerCancellationToken)
                    ?? throw Failure(
                        "Promotion.Scheduling",
                        "PROMOTION_SCHEDULE_ENTITLEMENT_MISSING",
                        500,
                        $"Due entitlement '{entitlementId}' no longer exists.",
                        "Restore the exact Promotion entitlement before resuming scheduled transitions.");
                var entitlement = RestoreEntitlement(row);
                if (!entitlement.SynchronizeTime(entitlement.AggregateRevision, nowUtc))
                {
                    return 0;
                }

                Apply(row, entitlement);
                var eventId = idSource.CreateId();
                var integrationEvent = PromotionContractMapper.ToEvent(entitlement, eventId, nowUtc);
                AddOutbox(PromotionOutboxMessageFactory.Create(
                    eventId,
                    PromotionIntegrationEventTypes.EntitlementChanged,
                    PromotionIntegrationEventContracts.EntitlementChanged,
                    integrationEvent,
                    nowUtc,
                    commandContext));
                await _dbContext.SaveChangesAsync(innerCancellationToken);
                return 1;
            }, cancellationToken);
        }
        finally
        {
            _dbContext.ChangeTracker.Clear();
        }
    }

    private async Task<int> SynchronizePlacementAsync(
        PlacementScheduleCandidate candidate,
        DateTimeOffset nowUtc,
        Guid systemActorId,
        PromotionCommandContext commandContext,
        IPromotionIdSource idSource,
        CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();
        var listingStream = $"{candidate.CatalogKey}:{candidate.ListingId:D}";
        var connectionOpened = false;
        var streamLockAcquired = false;
        try
        {
            await _dbContext.Database.OpenConnectionAsync(cancellationToken);
            connectionOpened = true;
            _ = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_lock(hashtextextended({listingStream}, 2));",
                cancellationToken);
            streamLockAcquired = true;
            return await ExecuteInTransactionAsync(async innerCancellationToken =>
            {
                var row = await _dbContext.Placements
                    .FromSqlInterpolated($$"""
                        SELECT *
                        FROM placements.sponsored_placement
                        WHERE id = {{candidate.PlacementId}}
                        FOR UPDATE
                        """)
                    .SingleOrDefaultAsync(innerCancellationToken)
                    ?? throw Failure(
                        "Promotion.Scheduling",
                        "PROMOTION_SCHEDULE_PLACEMENT_MISSING",
                        500,
                        $"Due placement '{candidate.PlacementId}' no longer exists.",
                        "Restore the exact Promotion placement before resuming scheduled transitions.");
                if (row.State is not (
                        (int)SponsoredPlacementState.Scheduled or
                        (int)SponsoredPlacementState.Active))
                {
                    return 0;
                }

                var placement = await RestorePlacementAsync(row, innerCancellationToken);
                if (!string.Equals(
                        placement.CurrentRevision.CatalogKey,
                        candidate.CatalogKey,
                        StringComparison.Ordinal) ||
                    placement.ListingId != candidate.ListingId)
                {
                    throw Failure(
                        "Promotion.Scheduling",
                        "PROMOTION_SCHEDULE_CANDIDATE_IDENTITY_DIVERGED",
                        500,
                        $"Placement '{placement.Id}' changed Catalog or listing identity after scheduler selection.",
                        "Stop the scheduler and repair the immutable placement owner identity.");
                }

                var entitlementRow = await _dbContext.Entitlements
                    .FromSqlInterpolated($$"""
                        SELECT *
                        FROM entitlements.promotion_entitlement
                        WHERE id = {{placement.EntitlementId}}
                        FOR SHARE
                        """)
                    .SingleOrDefaultAsync(innerCancellationToken)
                    ?? throw Failure(
                        "Promotion.Scheduling",
                        "PROMOTION_SCHEDULE_ENTITLEMENT_MISSING",
                        500,
                        $"Placement '{placement.Id}' references missing entitlement '{placement.EntitlementId}'.",
                        "Restore the exact Promotion entitlement before resuming scheduled transitions.");
                var entitlement = RestoreEntitlement(entitlementRow);
                var productRow = await _dbContext.Products
                    .FromSqlInterpolated($$"""
                        SELECT *
                        FROM products.promotion_product
                        WHERE product_key = {{placement.ProductKey}}
                        FOR SHARE
                        """)
                    .AsNoTracking()
                    .SingleOrDefaultAsync(innerCancellationToken)
                    ?? throw Failure(
                        "Promotion.Scheduling",
                        "PROMOTION_SCHEDULE_PRODUCT_MISSING",
                        500,
                        $"Placement '{placement.Id}' references missing product '{placement.ProductKey}'.",
                        "Restore the exact Promotion product before resuming scheduled transitions.");
                var product = await RestoreProductAsync(productRow, innerCancellationToken);
                var eligibility = await GetEligibilityAsync(
                    placement.CurrentRevision.CatalogKey,
                    placement.ListingId,
                    innerCancellationToken);
                if (!PromotionScheduledPlacementPolicy.Synchronize(
                        placement,
                        entitlement,
                        product,
                        eligibility,
                        systemActorId,
                        nowUtc))
                {
                    return 0;
                }

                row.State = (int)placement.State;
                row.ChangedAtUtc = placement.ChangedAtUtc;
                row.AuditReason = placement.AuditReason;
                row.AggregateRevision = placement.AggregateRevision;
                var capacityRows = await _dbContext.PlacementCapacity
                    .Where(existing => existing.PlacementId == placement.Id)
                    .ToArrayAsync(innerCancellationToken);
                _dbContext.PlacementCapacity.RemoveRange(capacityRows);
                AddCapacityRows(placement);
                var eventId = idSource.CreateId();
                var integrationEvent = PromotionContractMapper.ToEvent(placement, eventId, nowUtc);
                AddOutbox(PromotionOutboxMessageFactory.Create(
                    eventId,
                    PromotionIntegrationEventTypes.PlacementChanged,
                    PromotionIntegrationEventContracts.PlacementChanged,
                    integrationEvent,
                    nowUtc,
                    commandContext));
                await _dbContext.SaveChangesAsync(innerCancellationToken);
                return 1;
            }, cancellationToken);
        }
        finally
        {
            if (streamLockAcquired)
            {
                try
                {
                    _ = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_unlock(hashtextextended({listingStream}, 2));",
                        CancellationToken.None);
                }
                catch
                {
                    if (connectionOpened)
                    {
                        await _dbContext.Database.CloseConnectionAsync();
                        connectionOpened = false;
                    }

                    throw;
                }
            }

            if (connectionOpened)
            {
                await _dbContext.Database.CloseConnectionAsync();
            }

            _dbContext.ChangeTracker.Clear();
        }
    }

    private static void ValidateSchedulerInput(
        DateTimeOffset nowUtc,
        Guid systemActorId,
        int batchSize,
        IPromotionIdSource idSource)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "Promotion.Scheduling",
                "PROMOTION_SCHEDULE_TIMESTAMP_NOT_UTC",
                500,
                "Promotion scheduler timestamp must be normalized to UTC.",
                "Correct the Promotion worker clock before processing scheduled state.");
        }

        if (systemActorId == Guid.Empty)
        {
            throw Failure(
                "Promotion.Scheduling",
                "PROMOTION_SCHEDULE_ACTOR_REQUIRED",
                500,
                "Promotion scheduler requires one registered system actor identity.",
                "Configure PromotionWorker:SystemActorId with a non-empty internal actor ID.");
        }

        if (batchSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                batchSize,
                "Promotion scheduler batch size must be between 1 and 500.");
        }

        ArgumentNullException.ThrowIfNull(idSource);
    }

    private sealed record PlacementScheduleCandidate(
        Guid PlacementId,
        Guid ListingId,
        string CatalogKey);
}
