using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

public sealed record PublicListingSearchCriteria(
    string RequestedLocale,
    string? CategoryKey,
    string? DistrictKey,
    QueryListingKind? ListingKind,
    QueryContactKind? ContactKind);

public sealed record PublicSponsoredListingSnapshot(
    QueryPromotionPlacement Placement,
    QueryListingDocument Document);

public sealed record PublicReadPageSnapshot(
    PublicReadRevision Revision,
    QueryLocalePolicy LocalePolicy,
    IReadOnlyList<QueryListingDocument> Documents,
    IReadOnlyList<PublicSponsoredListingSnapshot> SponsoredDocuments,
    IReadOnlyDictionary<string, int> CategoryFacetCounts,
    IReadOnlyDictionary<string, int> DistrictFacetCounts,
    IReadOnlyDictionary<QueryListingKind, int> ListingKindFacetCounts,
    IReadOnlyDictionary<QueryContactKind, int> ContactKindFacetCounts);

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
        PublicListingSearchCriteria criteria,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken);

    Task<PublicReadDocumentSnapshot?> ReadByRouteAsync(
        string catalogKey,
        string routePath,
        CancellationToken cancellationToken);
}

public interface IQueryClock
{
    DateTimeOffset GetUtcNow();
}
