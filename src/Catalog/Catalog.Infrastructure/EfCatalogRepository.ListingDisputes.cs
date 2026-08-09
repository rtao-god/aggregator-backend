using Aggregator.Catalog.Application;
using Aggregator.Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Catalog.Infrastructure;

public sealed partial class EfCatalogRepository
{
    public async Task<ListingDispute> AddAsync(
        ListingDispute dispute,
        long expectedListingVersion,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispute);
        ArgumentNullException.ThrowIfNull(eventContext);
        if (expectedListingVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedListingVersion),
                expectedListingVersion,
                "Expected listing version must be greater than zero.");
        }

        try
        {
            return await ExecuteInTransactionAsync(async innerCancellationToken =>
            {
                var listingRow = await RequireLockedListingAsync(
                    dispute.ListingId,
                    innerCancellationToken);
                var listing = RehydrateListing(listingRow);
                if (listing.Version != expectedListingVersion)
                {
                    throw new CatalogConcurrencyException(
                        listing.Id,
                        expectedListingVersion,
                        listing.Version);
                }

                var existingOpenDispute = await _dbContext.ListingDisputes
                    .AsNoTracking()
                    .Where(row =>
                        row.ListingId == dispute.ListingId &&
                        row.State == (int)ListingDisputeState.Open)
                    .Select(row => (Guid?)row.Id)
                    .SingleOrDefaultAsync(innerCancellationToken);
                if (existingOpenDispute is { } openDisputeId)
                {
                    throw new CatalogConflictException(
                        $"Listing '{dispute.ListingId}' already has open dispute '{openDisputeId}'.");
                }

                _dbContext.ListingDisputes.Add(ToRow(dispute));
                var eligibilityOutbox = await CreateListingPromotionEligibilityOutboxAsync(
                    listing,
                    hasBlockingDispute: true,
                    dispute.OpenedAtUtc,
                    eventContext,
                    innerCancellationToken);
                AddOutbox(eligibilityOutbox);
                await _dbContext.SaveChangesAsync(innerCancellationToken);
                return dispute;
            }, cancellationToken);
        }
        catch (DbUpdateException exception)
            when (TryGetPostgresException(exception) is
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: "ux_catalog_listing_dispute_open",
                })
        {
            throw new CatalogConflictException(
                $"Listing '{dispute.ListingId}' already has an open dispute.");
        }
    }

    public async Task<ListingDispute?> GetAsync(
        Guid listingId,
        Guid disputeId,
        CancellationToken cancellationToken)
    {
        if (listingId == Guid.Empty || disputeId == Guid.Empty)
        {
            throw new ArgumentException(
                "Listing and dispute IDs must be non-empty UUIDs.");
        }

        var row = await _dbContext.ListingDisputes
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Id == disputeId &&
                    candidate.ListingId == listingId,
                cancellationToken);
        return row is null ? null : RehydrateDispute(row);
    }

    public async Task<ListingDispute> SaveAsync(
        ListingDispute dispute,
        long expectedStoredAggregateRevision,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispute);
        ArgumentNullException.ThrowIfNull(eventContext);
        if (expectedStoredAggregateRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedStoredAggregateRevision),
                expectedStoredAggregateRevision,
                "Expected stored dispute revision must be greater than zero.");
        }

        return await ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            // Every dispute/publication path locks the listing before its dispute state.
            // The shared order prevents resolve/open/publication deadlocks.
            var listingRow = await RequireLockedListingAsync(
                dispute.ListingId,
                innerCancellationToken);
            var row = await _dbContext.ListingDisputes
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM catalog.listing_dispute
                    WHERE id = {dispute.Id}
                      AND listing_id = {dispute.ListingId}
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(innerCancellationToken)
                ?? throw new CatalogNotFoundException("listing-dispute", dispute.Id);
            if (row.AggregateRevision != expectedStoredAggregateRevision)
            {
                throw new CatalogListingDisputeConcurrencyException(
                    dispute.Id,
                    expectedStoredAggregateRevision,
                    row.AggregateRevision);
            }

            EnsureStoredIdentity(row, dispute, expectedStoredAggregateRevision);
            row.State = (int)dispute.State;
            row.ResolutionReason = dispute.ResolutionReason;
            row.ResolvedByActorId = dispute.ResolvedByActorId;
            row.ResolvedAtUtc = dispute.ResolvedAtUtc;
            row.AggregateRevision = dispute.AggregateRevision;

            var listing = RehydrateListing(listingRow);
            var eligibilityOutbox = await CreateListingPromotionEligibilityOutboxAsync(
                listing,
                hasBlockingDispute: false,
                dispute.ResolvedAtUtc
                    ?? throw new CatalogInvariantException(
                        "Resolved listing dispute lacks its resolution timestamp."),
                eventContext,
                innerCancellationToken);
            AddOutbox(eligibilityOutbox);
            await _dbContext.SaveChangesAsync(innerCancellationToken);
            return dispute;
        }, cancellationToken);
    }

    private async Task<CatalogListingRow> RequireLockedListingAsync(
        Guid listingId,
        CancellationToken cancellationToken) =>
        await _dbContext.Listings
            .FromSqlInterpolated($"""
                SELECT *
                FROM catalog.listing
                WHERE id = {listingId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new CatalogNotFoundException("listing", listingId);

    private async Task EnsureNoOpenListingDisputesAsync(
        string catalogKey,
        Guid publicationId,
        IEnumerable<Guid> listingIds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        if (publicationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Publication ID is required for the dispute activation gate.",
                nameof(publicationId));
        }

        ArgumentNullException.ThrowIfNull(listingIds);
        var ids = listingIds
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        foreach (var listingId in ids)
        {
            var lockedListing = await RequireLockedListingAsync(
                listingId,
                cancellationToken);
            if (!string.Equals(
                    lockedListing.CatalogKey,
                    catalogKey,
                    StringComparison.Ordinal))
            {
                throw new CatalogConflictException(
                    $"Listing '{listingId}' does not belong to Catalog '{catalogKey}'.");
            }
        }

        if (ids.Length == 0)
        {
            return;
        }

        var blocked = await _dbContext.ListingDisputes
            .AsNoTracking()
            .Where(row =>
                ids.Contains(row.ListingId) &&
                row.State == (int)ListingDisputeState.Open)
            .OrderBy(row => row.ListingId)
            .Select(row => new { row.ListingId, row.Id })
            .ToArrayAsync(cancellationToken);
        if (blocked.Length > 0)
        {
            throw new CatalogPublicationActivationBlockedException(
                catalogKey,
                publicationId,
                CatalogPublicationActivationBlockReason.ListingDispute,
                requiredAction:
                    "Resolve every open Catalog listing dispute before creating or activating the publication.",
                detail:
                    "Publication contains listings with open disputes: " +
                    string.Join(
                        ", ",
                        blocked.Select(value =>
                            $"{value.ListingId:D}/{value.Id:D}")));
        }
    }

    private static CatalogListingDisputeRow ToRow(ListingDispute dispute) =>
        new()
        {
            Id = dispute.Id,
            ListingId = dispute.ListingId,
            State = (int)dispute.State,
            OpenReason = dispute.OpenReason,
            OpenedByActorId = dispute.OpenedByActorId,
            OpenedAtUtc = dispute.OpenedAtUtc,
            ResolutionReason = dispute.ResolutionReason,
            ResolvedByActorId = dispute.ResolvedByActorId,
            ResolvedAtUtc = dispute.ResolvedAtUtc,
            AggregateRevision = dispute.AggregateRevision,
        };

    private static ListingDispute RehydrateDispute(CatalogListingDisputeRow row) =>
        ListingDispute.Restore(new ListingDisputeSnapshot(
            row.Id,
            row.ListingId,
            Enum.IsDefined(typeof(ListingDisputeState), row.State)
                ? (ListingDisputeState)row.State
                : throw new CatalogInvariantException(
                    $"Persisted listing dispute '{row.Id}' has unsupported state '{row.State}'."),
            row.OpenReason,
            row.OpenedByActorId,
            row.OpenedAtUtc,
            row.ResolutionReason,
            row.ResolvedByActorId,
            row.ResolvedAtUtc,
            row.AggregateRevision));

    private static void EnsureStoredIdentity(
        CatalogListingDisputeRow stored,
        ListingDispute dispute,
        long expectedStoredAggregateRevision)
    {
        if (dispute.State != ListingDisputeState.Resolved ||
            dispute.AggregateRevision != checked(expectedStoredAggregateRevision + 1) ||
            stored.Id != dispute.Id ||
            stored.ListingId != dispute.ListingId ||
            stored.State != (int)ListingDisputeState.Open ||
            stored.OpenReason != dispute.OpenReason ||
            stored.OpenedByActorId != dispute.OpenedByActorId ||
            stored.OpenedAtUtc != dispute.OpenedAtUtc ||
            stored.ResolutionReason is not null ||
            stored.ResolvedByActorId is not null ||
            stored.ResolvedAtUtc is not null)
        {
            throw new CatalogInvariantException(
                $"Listing dispute '{dispute.Id}' transition diverges from its stored immutable opening evidence.");
        }
    }
}
