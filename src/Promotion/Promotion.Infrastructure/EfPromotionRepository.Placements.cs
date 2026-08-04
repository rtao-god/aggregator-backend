using Aggregator.Promotion.Application;
using Aggregator.Promotion.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Promotion.Infrastructure;

public sealed partial class EfPromotionRepository
{
    public async Task<SponsoredPlacement?> GetPlacementAsync(
        Guid placementId,
        CancellationToken cancellationToken)
    {
        var row = await _dbContext.Placements
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == placementId, cancellationToken);
        return row is null
            ? null
            : await RestorePlacementAsync(row, cancellationToken);
    }

    public async Task<IReadOnlyList<SponsoredPlacement>> ListPlacementsAsync(
        string catalogKey,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        var normalizedCatalogKey = catalogKey.Trim().ToLowerInvariant();
        var placementIds = await _dbContext.PlacementRevisions
            .AsNoTracking()
            .Join(
                _dbContext.Placements.AsNoTracking(),
                revision => revision.Id,
                placement => placement.CurrentRevisionId,
                (revision, placement) => new { Revision = revision, Placement = placement })
            .Where(item => item.Revision.CatalogKey == normalizedCatalogKey)
            .Where(item => item.Revision.StartsAtUtc < toUtc && fromUtc < item.Revision.EndsAtUtc)
            .OrderBy(item => item.Revision.StartsAtUtc)
            .ThenBy(item => item.Revision.CapacitySlot)
            .ThenBy(item => item.Placement.Id)
            .Select(item => item.Placement.Id)
            .ToArrayAsync(cancellationToken);
        var results = new List<SponsoredPlacement>(placementIds.Length);
        foreach (var placementId in placementIds)
        {
            var placement = await GetPlacementAsync(placementId, cancellationToken)
                ?? throw Failure(
                    "Promotion.Persistence",
                    "PROMOTION_PLACEMENT_DISAPPEARED",
                    500,
                    $"Promotion placement '{placementId}' disappeared while reading the calendar.",
                    "Retry the read after the responsible database transaction completes.");
            results.Add(placement);
        }

        return results;
    }

    public async Task<bool> HasPlacementConflictAsync(
        SponsoredPlacement candidate,
        Guid? excludedPlacementId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.ConsumesCapacity)
        {
            return false;
        }

        var revision = candidate.CurrentRevision;
        var locales = revision.LocaleScope.ToArray();
        return await _dbContext.PlacementCapacity
            .AsNoTracking()
            .Where(row => excludedPlacementId == null || row.PlacementId != excludedPlacementId)
            .Where(row => row.CatalogKey == revision.CatalogKey)
            .Where(row => row.ScopeType == (int)revision.ScopeType)
            .Where(row => row.ScopeKey == revision.ScopeKey)
            .Where(row => row.CapacitySlot == revision.CapacitySlot)
            .Where(row => locales.Contains(row.Locale))
            .AnyAsync(
                row =>
                    row.StartsAtUtc < revision.EffectiveWindow.EndsAtUtc &&
                    revision.EffectiveWindow.StartsAtUtc < row.EndsAtUtc,
                cancellationToken);
    }

    public Task<PromotionCommandResult<SponsoredPlacement>> AddPlacementAsync(
        SponsoredPlacement placement,
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        PromotionOutboxMessage outboxMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(outboxMessage);
        return ExecuteCommandAsync(
            commandIdentity,
            commandContext,
            innerCancellationToken =>
            {
                innerCancellationToken.ThrowIfCancellationRequested();
                _dbContext.Placements.Add(ToRow(placement));
                _dbContext.PlacementRevisions.Add(ToRow(placement.CurrentRevision));
                AddCapacityRows(placement);
                AddOutbox(outboxMessage);
                return Task.FromResult(placement);
            },
            cancellationToken);
    }

    public Task<PromotionCommandResult<SponsoredPlacement>> SavePlacementAsync(
        SponsoredPlacement placement,
        long expectedStoredAggregateRevision,
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        PromotionOutboxMessage outboxMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(outboxMessage);
        return ExecuteCommandAsync(
            commandIdentity,
            commandContext,
            async innerCancellationToken =>
            {
                var row = await _dbContext.Placements.SingleOrDefaultAsync(
                    candidate => candidate.Id == placement.Id,
                    innerCancellationToken)
                    ?? throw Failure(
                        "Promotion.Placements",
                        "PROMOTION_PLACEMENT_NOT_FOUND",
                        404,
                        $"Sponsored placement '{placement.Id}' was not found.",
                        "Reload the Promotion calendar before retrying the command.");
                EnsureStoredRevision(
                    row.AggregateRevision,
                    expectedStoredAggregateRevision,
                    "Sponsored placement",
                    placement.Id);
                row.State = (int)placement.State;
                row.CurrentRevisionId = placement.CurrentRevision.Id;
                row.ChangedAtUtc = placement.ChangedAtUtc;
                row.AuditReason = placement.AuditReason;
                row.AggregateRevision = placement.AggregateRevision;

                var revisionExists = await _dbContext.PlacementRevisions
                    .AnyAsync(candidate => candidate.Id == placement.CurrentRevision.Id, innerCancellationToken);
                if (!revisionExists)
                {
                    _dbContext.PlacementRevisions.Add(ToRow(placement.CurrentRevision));
                }

                var previousCapacity = await _dbContext.PlacementCapacity
                    .Where(candidate => candidate.PlacementId == placement.Id)
                    .ToArrayAsync(innerCancellationToken);
                _dbContext.PlacementCapacity.RemoveRange(previousCapacity);
                AddCapacityRows(placement);
                AddOutbox(outboxMessage);
                return placement;
            },
            cancellationToken);
    }

    private async Task<SponsoredPlacement> RestorePlacementAsync(
        SponsoredPlacementRow row,
        CancellationToken cancellationToken)
    {
        var revisionRow = await _dbContext.PlacementRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == row.CurrentRevisionId, cancellationToken)
            ?? throw Failure(
                "Promotion.Persistence",
                "PROMOTION_PLACEMENT_REVISION_MISSING",
                500,
                $"Sponsored placement '{row.Id}' points to missing revision '{row.CurrentRevisionId}'.",
                "Restore the exact placement revision from a verified database backup.");
        return SponsoredPlacement.Restore(
            row.Id,
            row.EntitlementId,
            row.ListingId,
            row.ProductKey,
            (SponsoredPlacementState)row.State,
            RestoreRevision(revisionRow),
            row.ChangedAtUtc,
            row.AuditReason,
            row.AggregateRevision);
    }

    private void AddCapacityRows(SponsoredPlacement placement)
    {
        if (!placement.ConsumesCapacity)
        {
            return;
        }

        var revision = placement.CurrentRevision;
        foreach (var locale in revision.LocaleScope.Order(StringComparer.Ordinal))
        {
            _dbContext.PlacementCapacity.Add(new SponsoredPlacementCapacityRow
            {
                PlacementId = placement.Id,
                PlacementRevisionId = revision.Id,
                CatalogKey = revision.CatalogKey,
                ScopeType = (int)revision.ScopeType,
                ScopeKey = revision.ScopeKey,
                Locale = locale,
                CapacitySlot = revision.CapacitySlot,
                StartsAtUtc = revision.EffectiveWindow.StartsAtUtc,
                EndsAtUtc = revision.EffectiveWindow.EndsAtUtc,
                PlacementState = (int)placement.State,
            });
        }
    }

    private static SponsoredPlacementRow ToRow(SponsoredPlacement placement) =>
        new()
        {
            Id = placement.Id,
            EntitlementId = placement.EntitlementId,
            ListingId = placement.ListingId,
            ProductKey = placement.ProductKey,
            State = (int)placement.State,
            CurrentRevisionId = placement.CurrentRevision.Id,
            ChangedAtUtc = placement.ChangedAtUtc,
            AuditReason = placement.AuditReason,
            AggregateRevision = placement.AggregateRevision,
        };

    private static SponsoredPlacementRevisionRow ToRow(SponsoredPlacementRevision revision) =>
        new()
        {
            Id = revision.Id,
            PlacementId = revision.PlacementId,
            RevisionNumber = revision.RevisionNumber,
            CatalogKey = revision.CatalogKey,
            ScopeType = (int)revision.ScopeType,
            ScopeKey = revision.ScopeKey,
            LocaleScopeJson = PromotionPersistenceJson.SerializeStringSet(revision.LocaleScope),
            StartsAtUtc = revision.EffectiveWindow.StartsAtUtc,
            EndsAtUtc = revision.EffectiveWindow.EndsAtUtc,
            PriorityBand = revision.PriorityBand,
            CapacitySlot = revision.CapacitySlot,
            PresentationLabelKey = revision.PresentationLabelKey,
            CreatedByActorId = revision.CreatedByActorId,
            CreatedAtUtc = revision.CreatedAtUtc,
            ContentDigest = revision.ContentDigest,
        };

    private static SponsoredPlacementRevision RestoreRevision(SponsoredPlacementRevisionRow row) =>
        SponsoredPlacementRevision.Create(
            row.Id,
            row.PlacementId,
            row.RevisionNumber,
            row.CatalogKey,
            (PlacementScopeType)row.ScopeType,
            row.ScopeKey,
            PromotionPersistenceJson.DeserializeStringSet(row.LocaleScopeJson),
            PromotionWindow.Create(row.StartsAtUtc, row.EndsAtUtc),
            row.PriorityBand,
            row.CapacitySlot,
            row.PresentationLabelKey,
            row.CreatedByActorId,
            row.CreatedAtUtc,
            row.ContentDigest);
}
