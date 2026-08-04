using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

public sealed record PublicReadPageSnapshot(
    PublicReadRevision Revision,
    IReadOnlyList<QueryListingDocument> Documents,
    IReadOnlyDictionary<string, int> CategoryFacetCounts);

public sealed record PublicReadDocumentSnapshot(
    PublicReadRevision Revision,
    QueryListingDocument? Document);

public interface IPublicQueryStore
{
    public Task<PublicReadPageSnapshot?> ReadPageAsync(
        string catalogKey,
        Guid? afterListingId,
        int maximumDocuments,
        string? categoryKey,
        CancellationToken cancellationToken);

    public Task<PublicReadDocumentSnapshot?> ReadByRouteAsync(
        string catalogKey,
        string routePath,
        CancellationToken cancellationToken);
}
