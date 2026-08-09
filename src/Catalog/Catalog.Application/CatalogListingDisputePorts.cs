using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>Catalog-owned persistence boundary for listing disputes and their eligibility effects.</summary>
public interface ICatalogListingDisputeRepository
{
    public Task<ListingDispute> AddAsync(
        ListingDispute dispute,
        long expectedListingVersion,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken);

    public Task<ListingDispute?> GetAsync(
        Guid listingId,
        Guid disputeId,
        CancellationToken cancellationToken);

    public Task<ListingDispute> SaveAsync(
        ListingDispute dispute,
        long expectedStoredAggregateRevision,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken);
}
