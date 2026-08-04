using Aggregator.Promotion.Application;
using Aggregator.Promotion.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Promotion.Infrastructure;

public sealed partial class EfPromotionRepository
{
    public async Task<PromotionEntitlement?> GetEntitlementAsync(
        Guid entitlementId,
        CancellationToken cancellationToken)
    {
        var row = await _dbContext.Entitlements
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == entitlementId, cancellationToken);
        return row is null ? null : RestoreEntitlement(row);
    }

    public async Task<IReadOnlyList<PromotionEntitlement>> ListEntitlementsAsync(
        Guid listingId,
        CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Entitlements
            .AsNoTracking()
            .Where(candidate => candidate.ListingId == listingId)
            .OrderBy(candidate => candidate.StartsAtUtc)
            .ThenBy(candidate => candidate.Id)
            .ToArrayAsync(cancellationToken);
        return rows.Select(RestoreEntitlement).ToArray();
    }

    public Task<PromotionCommandResult<PromotionEntitlement>> AddEntitlementAsync(
        PromotionEntitlement entitlement,
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        PromotionOutboxMessage outboxMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        ArgumentNullException.ThrowIfNull(outboxMessage);
        return ExecuteCommandAsync(
            commandIdentity,
            commandContext,
            innerCancellationToken =>
            {
                innerCancellationToken.ThrowIfCancellationRequested();
                _dbContext.Entitlements.Add(ToRow(entitlement));
                AddOutbox(outboxMessage);
                return Task.FromResult(entitlement);
            },
            cancellationToken);
    }

    public Task<PromotionCommandResult<PromotionEntitlement>> SaveEntitlementAsync(
        PromotionEntitlement entitlement,
        long expectedStoredAggregateRevision,
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        PromotionOutboxMessage outboxMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        ArgumentNullException.ThrowIfNull(outboxMessage);
        return ExecuteCommandAsync(
            commandIdentity,
            commandContext,
            async innerCancellationToken =>
            {
                var row = await _dbContext.Entitlements.SingleOrDefaultAsync(
                    candidate => candidate.Id == entitlement.Id,
                    innerCancellationToken)
                    ?? throw Failure(
                        "Promotion.Entitlements",
                        "PROMOTION_ENTITLEMENT_NOT_FOUND",
                        404,
                        $"Promotion entitlement '{entitlement.Id}' was not found.",
                        "Reload the listing entitlement inventory before retrying the command.");
                EnsureStoredRevision(
                    row.AggregateRevision,
                    expectedStoredAggregateRevision,
                    "Promotion entitlement",
                    entitlement.Id);
                Apply(row, entitlement);
                AddOutbox(outboxMessage);
                return entitlement;
            },
            cancellationToken);
    }

    private static PromotionEntitlementRow ToRow(PromotionEntitlement entitlement)
    {
        var row = new PromotionEntitlementRow
        {
            Id = entitlement.Id,
            ListingId = entitlement.ListingId,
            ProductKey = entitlement.ProductKey,
            SourceType = (int)entitlement.SourceType,
            ExternalReference = entitlement.ExternalReference,
            StartsAtUtc = entitlement.EffectiveWindow.StartsAtUtc,
            EndsAtUtc = entitlement.EffectiveWindow.EndsAtUtc,
            State = (int)entitlement.State,
            CreatedByActorId = entitlement.CreatedByActorId,
            AuditReason = entitlement.AuditReason,
            CreatedAtUtc = entitlement.CreatedAtUtc,
            ChangedAtUtc = entitlement.ChangedAtUtc,
            AggregateRevision = entitlement.AggregateRevision,
        };
        return row;
    }

    private static void Apply(
        PromotionEntitlementRow row,
        PromotionEntitlement entitlement)
    {
        row.State = (int)entitlement.State;
        row.AuditReason = entitlement.AuditReason;
        row.ChangedAtUtc = entitlement.ChangedAtUtc;
        row.AggregateRevision = entitlement.AggregateRevision;
    }

    private static PromotionEntitlement RestoreEntitlement(PromotionEntitlementRow row) =>
        PromotionEntitlement.Restore(
            row.Id,
            row.ListingId,
            row.ProductKey,
            (PromotionEntitlementSourceType)row.SourceType,
            row.ExternalReference,
            PromotionWindow.Create(row.StartsAtUtc, row.EndsAtUtc),
            (PromotionEntitlementState)row.State,
            row.CreatedByActorId,
            row.AuditReason,
            row.CreatedAtUtc,
            row.ChangedAtUtc,
            row.AggregateRevision);
}
