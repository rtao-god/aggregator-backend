using System.Data;
using Aggregator.Promotion.Application;
using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Promotion.Infrastructure;

public sealed partial class EfPromotionRepository
{
    /// <summary>
    /// Pauses every active or scheduled placement invalidated by the current Catalog eligibility revision.
    /// The placement transitions, capacity release, and Promotion outbox messages commit atomically.
    /// </summary>
    public async Task<int> PauseIneligiblePlacementsAsync(
        PromotionEligibilityPlacementReconciliationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Eligibility);
        ValidateEligibilityReconciliationRequest(request);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            var currentEligibility = await _dbContext.ListingEligibility
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row =>
                        row.CatalogKey == request.Eligibility.CatalogKey &&
                        row.ListingId == request.Eligibility.ListingId,
                    cancellationToken)
                ?? throw new PromotionApplicationException(
                    "Promotion.EligibilityProjection",
                    "PROMOTION_ELIGIBILITY_RECONCILIATION_PROJECTION_MISSING",
                    500,
                    "Promotion cannot reconcile placements because the current eligibility projection is absent.",
                    "Restore the current Catalog eligibility checkpoint before replaying the event.");
            if (currentEligibility.SourceRevision > request.Eligibility.SourceRevision)
            {
                await transaction.CommitAsync(cancellationToken);
                return 0;
            }

            if (currentEligibility.SourceRevision < request.Eligibility.SourceRevision)
            {
                throw new PromotionApplicationException(
                    "Promotion.EligibilityProjection",
                    "PROMOTION_ELIGIBILITY_RECONCILIATION_PROJECTION_BEHIND",
                    503,
                    $"Promotion eligibility checkpoint '{currentEligibility.SourceRevision}' trails reconciliation revision '{request.Eligibility.SourceRevision}'.",
                    "Apply the exact missing Catalog eligibility revisions before reconciling placements.");
            }

            EnsureCurrentEligibilityMatches(currentEligibility, request.Eligibility);
            var placementRows = await _dbContext.Placements
                .Where(row =>
                    row.ListingId == request.Eligibility.ListingId &&
                    (row.State == (int)SponsoredPlacementState.Scheduled ||
                     row.State == (int)SponsoredPlacementState.Active))
                .OrderBy(row => row.Id)
                .ToArrayAsync(cancellationToken);
            var products = new Dictionary<string, PromotionProduct>(StringComparer.Ordinal);
            var commandContext = new PromotionCommandContext(
                PromotionActor.Create(request.SystemActorId),
                request.CorrelationId,
                request.CausationId);
            var changed = 0;
            foreach (var row in placementRows)
            {
                var placement = await RestorePlacementAsync(row, cancellationToken);
                if (request.ChangedAtUtc < placement.ChangedAtUtc)
                {
                    throw new PromotionApplicationException(
                        "Promotion.EligibilityProjection",
                        "PROMOTION_ELIGIBILITY_RECONCILIATION_TIME_REGRESSION",
                        503,
                        $"Catalog eligibility reconciliation time precedes placement '{placement.Id}' state time.",
                        "Retry the exact Catalog event after the Promotion owner clock advances.",
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["placementId"] = placement.Id,
                            ["placementChangedAtUtc"] = placement.ChangedAtUtc,
                            ["reconciliationChangedAtUtc"] = request.ChangedAtUtc,
                            ["eligibilityRevision"] = request.Eligibility.SourceRevision,
                        });
                }

                if (!products.TryGetValue(placement.ProductKey, out var product))
                {
                    product = await GetProductByKeyAsync(
                        placement.ProductKey,
                        cancellationToken)
                        ?? throw new PromotionApplicationException(
                            "Promotion.Products",
                            "PROMOTION_ELIGIBILITY_PRODUCT_MISSING",
                            500,
                            $"Placement '{placement.Id}' references missing product '{placement.ProductKey}'.",
                            "Restore the exact Promotion product before replaying Catalog eligibility events.");
                    products.Add(product.Key, product);
                }

                if (!placement.PauseWhenCatalogIneligible(
                        request.Eligibility,
                        product,
                        request.SystemActorId,
                        request.ChangedAtUtc))
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
                var eventId = _idSource.CreateId();
                var integrationEvent = PromotionContractMapper.ToEvent(
                    placement,
                    eventId,
                    request.ChangedAtUtc);
                AddOutbox(PromotionOutboxMessageFactory.Create(
                    eventId,
                    PromotionIntegrationEventTypes.PlacementChanged,
                    PromotionIntegrationEventContracts.PlacementChanged,
                    integrationEvent,
                    request.ChangedAtUtc,
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
            throw new PromotionApplicationException(
                "Promotion.EligibilityProjection",
                "PROMOTION_ELIGIBILITY_RECONCILIATION_CONFLICT",
                503,
                "A placement changed concurrently with Catalog eligibility reconciliation.",
                "Replay the exact Catalog eligibility event against the current Promotion state.",
                innerException: exception);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void EnsureCurrentEligibilityMatches(
        ListingPromotionEligibilityRow current,
        ListingPromotionEligibility expected)
    {
        var currentCapabilities = PromotionPersistenceJson.DeserializeStringSet(
                current.ContactCapabilitiesJson)
            .ToHashSet(StringComparer.Ordinal);
        var currentCategories = PromotionPersistenceJson.DeserializeStringSet(
                current.CategoryKeysJson)
            .ToHashSet(StringComparer.Ordinal);
        if (current.IsPublished != expected.IsPublished ||
            current.IsArchived != expected.IsArchived ||
            current.HasBlockingDispute != expected.HasBlockingDispute ||
            current.HasVerifiedContact != expected.HasVerifiedContact ||
            !currentCapabilities.SetEquals(expected.ContactCapabilities) ||
            !currentCategories.SetEquals(expected.CategoryKeys) ||
            !string.Equals(current.DistrictKey, expected.DistrictKey, StringComparison.Ordinal) ||
            current.ChangedAtUtc != expected.ChangedAtUtc)
        {
            throw new PromotionApplicationException(
                "Promotion.EligibilityProjection",
                "PROMOTION_ELIGIBILITY_RECONCILIATION_PROJECTION_DIVERGED",
                500,
                "Promotion current eligibility facts diverge from the event selected for placement reconciliation.",
                "Stop placement mutations and rebuild the Promotion eligibility projection from Catalog events.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalogKey"] = expected.CatalogKey,
                    ["listingId"] = expected.ListingId,
                    ["eligibilityRevision"] = expected.SourceRevision,
                });
        }
    }

    private static void ValidateEligibilityReconciliationRequest(
        PromotionEligibilityPlacementReconciliationRequest request)
    {
        if (request.SystemActorId == Guid.Empty ||
            request.CausationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.CorrelationId) ||
            request.CorrelationId.Length > 128 ||
            request.CorrelationId.Any(char.IsControl) ||
            request.ChangedAtUtc.Offset != TimeSpan.Zero ||
            request.ChangedAtUtc < request.Eligibility.ChangedAtUtc)
        {
            throw new PromotionApplicationException(
                "Promotion.EligibilityProjection",
                "PROMOTION_ELIGIBILITY_RECONCILIATION_INPUT_INVALID",
                500,
                "Promotion eligibility reconciliation received invalid owner context.",
                "Correct the Promotion worker composition before replaying the Catalog event.");
        }
    }
}
