using Aggregator.Promotion.Application;
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

    public Task UpsertEligibilityAsync(
        ListingPromotionEligibility eligibility,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        return ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            var row = await _dbContext.ListingEligibility.SingleOrDefaultAsync(
                candidate =>
                    candidate.CatalogKey == eligibility.CatalogKey &&
                    candidate.ListingId == eligibility.ListingId,
                innerCancellationToken);
            if (row is null)
            {
                _dbContext.ListingEligibility.Add(ToRow(eligibility));
                await _dbContext.SaveChangesAsync(innerCancellationToken);
                return true;
            }

            if (eligibility.SourceRevision < row.SourceRevision)
            {
                throw Failure(
                    "Promotion.EligibilityProjection",
                    "PROMOTION_ELIGIBILITY_EVENT_STALE",
                    409,
                    $"Listing eligibility revision '{eligibility.SourceRevision}' trails stored revision '{row.SourceRevision}'.",
                    "Replay only Catalog eligibility events newer than the stored source revision.");
            }

            if (eligibility.SourceRevision == row.SourceRevision)
            {
                if (!EligibilityEquals(RestoreEligibility(row), eligibility))
                {
                    throw Failure(
                        "Promotion.EligibilityProjection",
                        "PROMOTION_ELIGIBILITY_REVISION_DIVERGED",
                        409,
                        "The same Catalog eligibility revision arrived with different projection content.",
                        "Block the consumer and inspect the producer event payload before replay.");
                }

                return false;
            }

            Apply(row, eligibility);
            await _dbContext.SaveChangesAsync(innerCancellationToken);
            return true;
        }, cancellationToken);
    }

    private static ListingPromotionEligibilityRow ToRow(ListingPromotionEligibility eligibility)
    {
        var row = new ListingPromotionEligibilityRow
        {
            CatalogKey = eligibility.CatalogKey,
            ListingId = eligibility.ListingId,
            IsPublished = eligibility.IsPublished,
            IsArchived = eligibility.IsArchived,
            HasBlockingDispute = eligibility.HasBlockingDispute,
            HasVerifiedContact = eligibility.HasVerifiedContact,
            ContactCapabilitiesJson = PromotionPersistenceJson.SerializeStringSet(eligibility.ContactCapabilities),
            CategoryKeysJson = PromotionPersistenceJson.SerializeStringSet(eligibility.CategoryKeys),
            DistrictKey = eligibility.DistrictKey,
            SourceRevision = eligibility.SourceRevision,
            ChangedAtUtc = eligibility.ChangedAtUtc,
        };
        return row;
    }

    private static void Apply(
        ListingPromotionEligibilityRow row,
        ListingPromotionEligibility eligibility)
    {
        row.IsPublished = eligibility.IsPublished;
        row.IsArchived = eligibility.IsArchived;
        row.HasBlockingDispute = eligibility.HasBlockingDispute;
        row.HasVerifiedContact = eligibility.HasVerifiedContact;
        row.ContactCapabilitiesJson = PromotionPersistenceJson.SerializeStringSet(eligibility.ContactCapabilities);
        row.CategoryKeysJson = PromotionPersistenceJson.SerializeStringSet(eligibility.CategoryKeys);
        row.DistrictKey = eligibility.DistrictKey;
        row.SourceRevision = eligibility.SourceRevision;
        row.ChangedAtUtc = eligibility.ChangedAtUtc;
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

    private static bool EligibilityEquals(
        ListingPromotionEligibility left,
        ListingPromotionEligibility right) =>
        string.Equals(left.CatalogKey, right.CatalogKey, StringComparison.Ordinal) &&
        left.ListingId == right.ListingId &&
        left.IsPublished == right.IsPublished &&
        left.IsArchived == right.IsArchived &&
        left.HasBlockingDispute == right.HasBlockingDispute &&
        left.HasVerifiedContact == right.HasVerifiedContact &&
        left.ContactCapabilities.SetEquals(right.ContactCapabilities) &&
        left.CategoryKeys.SetEquals(right.CategoryKeys) &&
        string.Equals(left.DistrictKey, right.DistrictKey, StringComparison.Ordinal) &&
        left.SourceRevision == right.SourceRevision &&
        left.ChangedAtUtc == right.ChangedAtUtc;
}
