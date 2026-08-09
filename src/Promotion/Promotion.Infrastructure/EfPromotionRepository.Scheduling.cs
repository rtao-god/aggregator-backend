using System.Data;
using Aggregator.Promotion.Application;
using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Promotion.Infrastructure;

public sealed partial class EfPromotionRepository
{
    /// <summary>
    /// Advances due entitlement and placement clocks and persists each resulting producer event in the
    /// Promotion outbox in the same owner transaction.
    /// </summary>
    public async Task<int> SynchronizeDueAsync(
        DateTimeOffset nowUtc,
        Guid systemActorId,
        int batchSize,
        IPromotionIdSource idSource,
        CancellationToken cancellationToken)
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
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            var changed = 0;
            var remaining = batchSize;
            var commandContext = PromotionCommandContext.Start(
                PromotionActor.Create(systemActorId),
                $"promotion-scheduler:{nowUtc:yyyyMMddHHmmssfffffff}");

            var entitlementRows = await _dbContext.Entitlements
                .Where(row =>
                    row.State != (int)PromotionEntitlementState.Paused &&
                    row.State != (int)PromotionEntitlementState.Revoked &&
                    row.State != (int)PromotionEntitlementState.Expired &&
                    (row.StartsAtUtc <= nowUtc || row.EndsAtUtc <= nowUtc))
                .OrderBy(row => row.EndsAtUtc)
                .ThenBy(row => row.StartsAtUtc)
                .ThenBy(row => row.Id)
                .Take(remaining)
                .ToArrayAsync(cancellationToken);
            foreach (var row in entitlementRows)
            {
                var entitlement = RestoreEntitlement(row);
                if (!entitlement.SynchronizeTime(entitlement.AggregateRevision, nowUtc))
                {
                    continue;
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
                changed++;
                remaining--;
                if (remaining == 0)
                {
                    break;
                }
            }

            if (remaining > 0)
            {
                var placementRows = await _dbContext.Placements
                    .Where(row =>
                        row.State != (int)SponsoredPlacementState.Paused &&
                        row.State != (int)SponsoredPlacementState.Ended &&
                        row.State != (int)SponsoredPlacementState.Revoked)
                    .OrderBy(row => row.ChangedAtUtc)
                    .ThenBy(row => row.Id)
                    .Take(remaining)
                    .ToArrayAsync(cancellationToken);
                foreach (var row in placementRows)
                {
                    var placement = await RestorePlacementAsync(row, cancellationToken);
                    var revision = placement.CurrentRevision;
                    if (revision.EffectiveWindow.StartsAtUtc > nowUtc &&
                        revision.EffectiveWindow.EndsAtUtc > nowUtc)
                    {
                        continue;
                    }

                    var entitlementRow = await _dbContext.Entitlements
                        .SingleOrDefaultAsync(
                            candidate => candidate.Id == placement.EntitlementId,
                            cancellationToken)
                        ?? throw Failure(
                            "Promotion.Scheduling",
                            "PROMOTION_SCHEDULE_ENTITLEMENT_MISSING",
                            500,
                            $"Placement '{placement.Id}' references missing entitlement '{placement.EntitlementId}'.",
                            "Restore the exact Promotion entitlement before resuming scheduled transitions.");
                    var entitlement = RestoreEntitlement(entitlementRow);
                    var productRow = await _dbContext.Products
                        .AsNoTracking()
                        .SingleOrDefaultAsync(
                            candidate => candidate.ProductKey == placement.ProductKey,
                            cancellationToken)
                        ?? throw Failure(
                            "Promotion.Scheduling",
                            "PROMOTION_SCHEDULE_PRODUCT_MISSING",
                            500,
                            $"Placement '{placement.Id}' references missing product '{placement.ProductKey}'.",
                            "Restore the exact Promotion product before resuming scheduled transitions.");
                    var product = await RestoreProductAsync(productRow, cancellationToken);
                    var eligibility = await GetEligibilityAsync(
                        revision.CatalogKey,
                        placement.ListingId,
                        cancellationToken);
                    if (!PromotionScheduledPlacementPolicy.Synchronize(
                            placement,
                            entitlement,
                            product,
                            eligibility,
                            systemActorId,
                            nowUtc))
                    {
                        continue;
                    }

                    row.State = (int)placement.State;
                    row.ChangedAtUtc = placement.ChangedAtUtc;
                    row.AuditReason = placement.AuditReason;
                    row.AggregateRevision = placement.AggregateRevision;
                    var capacityRows = await _dbContext.PlacementCapacity
                        .Where(candidate => candidate.PlacementId == placement.Id)
                        .ToArrayAsync(cancellationToken);
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
                    changed++;
                }
            }

            if (changed > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return changed;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
