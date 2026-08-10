namespace Aggregator.Query.Contracts;

/// <summary>Public route kinds that can participate in the Query-owned SEO read model.</summary>
public enum PublicSeoRouteKindContract
{
    Listing = 1,
    Category = 2,
    EditorialLanding = 3,
}

/// <summary>One exact locale/path member of a reciprocal hreflang group.</summary>
public sealed record PublicHreflangLinkDto(
    string Locale,
    string Path);

/// <summary>
/// One Query-owned sitemap record from an exact active public-read revision.
/// The record contains typed route data; callers do not provide arbitrary JSON or indexability flags.
/// </summary>
public sealed record PublicSitemapRecordDto(
    PublicSeoRouteKindContract RouteKind,
    string CatalogKey,
    string Locale,
    string Path,
    string CanonicalPath,
    IReadOnlyList<PublicHreflangLinkDto> Hreflang,
    DateTimeOffset LastModifiedAtUtc);

/// <summary>Stable, revision-bound page of sitemap records.</summary>
public sealed record PublicSitemapPageDto(
    Guid PublicReadRevisionId,
    IReadOnlyList<PublicSitemapRecordDto> Items,
    string? NextCursor);
