using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>
/// Persists the Catalog-owned suppression aggregate and its transactional public safety event.
/// </summary>
public interface ICatalogVisibilitySuppressionRepository
{
    public Task EnsureTargetExistsAsync(
        CatalogKey catalogKey,
        PublicVisibilitySuppressionTarget target,
        CancellationToken cancellationToken);

    public Task<PublicVisibilitySuppression?> GetAsync(
        Guid suppressionId,
        CancellationToken cancellationToken);

    public Task CreateActiveAsync(
        PublicVisibilitySuppression requested,
        PublicVisibilitySuppression active,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken);

    public Task ResolveAsync(
        PublicVisibilitySuppression resolved,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken);
}
