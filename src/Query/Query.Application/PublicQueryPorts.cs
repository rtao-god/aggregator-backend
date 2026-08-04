using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

public sealed record PublicReadPageSnapshot(
    PublicReadRevision Revision,
    QueryLocalePolicy LocalePolicy,
    IReadOnlyList<QueryListingDocument> Documents,
    IReadOnlyDictionary<string, int> CategoryFacetCounts);

public sealed record PublicReadDocumentSnapshot(
    PublicReadRevision Revision,
    QueryLocalePolicy LocalePolicy,
    QueryListingDocument? Document);

public interface IPublicQueryStore
{
    Task<PublicReadPageSnapshot?> ReadPageAsync(
        string catalogKey,
        Guid? afterListingId,
        int maximumDocuments,
        string? categoryKey,
        CancellationToken cancellationToken);

    Task<PublicReadDocumentSnapshot?> ReadByRouteAsync(
        string catalogKey,
        string routePath,
        CancellationToken cancellationToken);
}
