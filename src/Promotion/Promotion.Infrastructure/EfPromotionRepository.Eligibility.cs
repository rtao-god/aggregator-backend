using Aggregator.Promotion.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Promotion.Infrastructure;

public sealed partial class EfPromotionRepository
{
    public async Task<ListingPromotionEligibility?> GetEligibilityAsync(
        string catalogKey,
        Guid listingId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        var normalizedCatalogKey = catalogKey.Trim().ToLowerInvariant();
        var row = await _dbContext.ListingEligibility
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.CatalogKey == normalizedCatalogKey &&
                    candidate.ListingId == listingId,
                cancellationToken);
        return row is null ? null : RestoreEligibility(row);
    }

    private static ListingPromotionEligibility RestoreEligibility(ListingPromotionEligibilityRow row) =>
        ListingPromotionEligibility.Create(
            row.CatalogKey,
            row.ListingId,
            row.IsPublished,
            row.IsArchived,
            row.HasBlockingDispute,
            row.HasVerifiedContact,
            PromotionPersistenceJson.DeserializeStringSet(row.ContactCapabilitiesJson),
            PromotionPersistenceJson.DeserializeStringSet(row.CategoryKeysJson),
            row.DistrictKey,
            row.SourceRevision,
            row.ChangedAtUtc);
}
