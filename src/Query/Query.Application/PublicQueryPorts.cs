using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

public sealed record PublicSponsoredListingSnapshot(
    QueryPromotionPlacement Placement,
    QueryListingDocument Document);

public sealed record PublicReadPageSnapshot(
    PublicReadRevision Revision,
    QueryLocalePolicy LocalePolicy,
    IReadOnlyList<QueryListingDocument> Documents,
    IReadOnlyList<PublicSponsoredListingSnapshot> SponsoredDocuments,
    IReadOnlyDictionary<string, int> CategoryFacetCounts);

public sealed record PublicReadDocumentSnapshot(
    PublicReadRevision Revision,
    QueryLocalePolicy LocalePolicy,
    QueryListingDocument? Document);

public interface IPublicQueryStore
{
    public Task<PublicReadPageSnapshot?> ReadPageAsync(
        string catalogKey,
        Guid? afterListingId,
        int maximumDocuments,
        string? categoryKey,
        string requestedLocale,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken);

    public Task<PublicReadDocumentSnapshot?> ReadByRouteAsync(
        string catalogKey,
        string routePath,
        CancellationToken cancellationToken);
}
