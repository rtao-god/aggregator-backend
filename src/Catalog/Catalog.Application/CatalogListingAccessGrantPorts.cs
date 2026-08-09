using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>
/// Persists one exact ListingAccessGrant aggregate revision and both claim/grant outbox effects atomically.
/// </summary>
public interface ICatalogListingAccessGrantRepository
{
    public Task<ListingAccessGrant?> GetByClaimAsync(
        Guid claimId,
        CancellationToken cancellationToken);

    public Task CompleteVerificationAsync(
        ListingClaim claim,
        ListingAccessGrant grant,
        CatalogOutboxMessage claimOutboxMessage,
        CatalogOutboxMessage grantOutboxMessage,
        CancellationToken cancellationToken);

    public Task CompleteRevocationAsync(
        ListingClaim claim,
        ListingAccessGrant grant,
        CatalogOutboxMessage claimOutboxMessage,
        CatalogOutboxMessage grantOutboxMessage,
        CancellationToken cancellationToken);
}
