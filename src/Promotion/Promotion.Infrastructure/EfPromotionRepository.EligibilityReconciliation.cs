using System.Data;
using Aggregator.Promotion.Application;
using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Promotion.Infrastructure;

public sealed partial class EfPromotionRepository
{
    /// <summary>
    /// Pauses active or scheduled placements invalidated by one exact Catalog eligibility revision.
    /// Eligibility recovery never resumes a placement; resume remains an explicit Promotion command.
    /// </summary>
    public async Task<int> PauseIneligiblePlacementsAsync(
        ListingPromotionEligibility eligibility,
        PromotionCommandContext commandContext,
        DateTimeOffset changedAtUtc,
        IPromotionIdSource idSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        ArgumentNullException.ThrowIfNull(commandContext);
        ArgumentNullException.ThrowIfNull(idSource);
        if (changedAtUtc.Offset != TimeSpan.Zero || changedAtUtc < eligibility.ChangedAtUtc)
        {
            throw Failure(
                "Promotion.EligibilityReconciliation",
                "PROMOTION_ELIGIBILITY_RECONCILIATION_TIMESTAMP_INVALID",
                500,
                "Promotion eligibility reconciliation requires a UTC timestamp not earlier than the Catalog event.",
                "Correct Promotion worker clock synchronization before replaying the event.");
        }

        if (commandContext.CausationId is not { } causationId || causationId == Guid.Empty)
        {
            throw Failure(
                "Promotion.EligibilityReconciliation",
                "PROMOTION_ELIGIBILITY_RECONCILIATION_CAUSATION_REQUIRED",
                500,
                "Promotion eligibility reconciliation requires the exact Catalog message identity as causation.",
                "Propagate the producer message ID before creating automatic placement effects.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            var placementRows = await _dbContext.Placements
                .Where(row =>
                    row.ListingId == eligibility.ListingId &&
                    (row.State == (int)SponsoredPlacementState.Scheduled ||
                     row.State == (int)SponsoredPlacementState.Active))
                .OrderBy(row => row.Id)
                .ToArrayAsync(cancellationToken);
            var products = new Dictionary<string, PromotionProduct>(StringComparer.Ordinal);
            var changed = 0;
            foreach (var row in placementRows)
            {
                var placement = await RestorePlacementAsync(row, cancellationToken);
                if (!string.Equals(
                        placement.CurrentRevision.CatalogKey,
                        eligibility.CatalogKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!products.TryGetValue(placement.ProductKey, out var product))
                {
                    var productRow = await _dbContext.Products
                        .AsNoTracking()
                        .SingleOrDefaultAsync(
                            candidate => candidate.ProductKey == placement.ProductKey,
                            cancellationToken)
                        ?? throw Failure(
                            "Promotion.EligibilityReconciliation",
                            "PROMOTION_ELIGIBILITY_PRODUCT_MISSING",
                            500,
                            $"Placement '{placement.Id}' references missing product '{placement.ProductKey}'.",
                            "Restore the exact Promotion product before replaying the Catalog eligibility event.");
                    product = await RestoreProductAsync(productRow, cancellationToken);
                    products.Add(product.Key, product);
                }

                if (!placement.PauseWhenCatalogIneligible(
                        eligibility,
                        product,
                        commandContext.Actor.Id,
                        changedAtUtc))
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

                var eventId = idSource.CreateId();
                var integrationEvent = PromotionContractMapper.ToEvent(
                    placement,
                    eventId,
                    changedAtUtc);
                AddOutbox(PromotionOutboxMessageFactory.Create(
                    eventId,
                    PromotionIntegrationEventTypes.PlacementChanged,
                    PromotionIntegrationEventContracts.PlacementChanged,
                    integrationEvent,
                    changedAtUtc,
                    commandContext));
                changed++;
            }

            if (changed > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return changed;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _dbContext.ChangeTracker.Clear();
            throw Failure(
                "Promotion.EligibilityReconciliation",
                "PROMOTION_ELIGIBILITY_RECONCILIATION_CONFLICT",
                503,
                "A Promotion placement changed while applying Catalog eligibility.",
                "Replay the exact Catalog event after the concurrent placement command completes.",
                exception);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _dbContext.ChangeTracker.Clear();
            throw Failure(
                "Promotion.EligibilityReconciliation",
                "PROMOTION_ELIGIBILITY_RECONCILIATION_SERIALIZATION_CONFLICT",
                503,
                "Promotion eligibility reconciliation conflicted with another serializable owner transaction.",
                "Replay the exact Catalog event after the concurrent Promotion transaction completes.",
                exception);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
