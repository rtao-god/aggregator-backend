using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

/// <summary>Query projection input for one authored public route candidate.</summary>
public sealed record PublicSeoRouteSource(
    QuerySeoRouteKind RouteKind,
    string RouteGroupKey,
    string CatalogKey,
    string Locale,
    string Path,
    DateTimeOffset LastModifiedAtUtc,
    bool IsDraft,
    string? RedirectTargetPath,
    bool IsSuppressed,
    Guid? RedirectSourcePublicationId = null,
    string? RedirectReason = null,
    DateTimeOffset? RedirectCreatedAtUtc = null);

/// <summary>Single mapper from Query route sources to indexable sitemap documents.</summary>
public static class PublicSitemapDocumentBuilder
{
    public static IReadOnlyList<QuerySitemapDocument> Build(
        IReadOnlyCollection<PublicSeoRouteSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var normalized = sources
            .Select(Normalize)
            .OrderBy(source => source.CatalogKey.Value, StringComparer.Ordinal)
            .ThenBy(source => source.RouteKind)
            .ThenBy(source => source.RouteGroupKey, StringComparer.Ordinal)
            .ThenBy(source => source.Locale.Value, StringComparer.Ordinal)
            .ThenBy(source => source.Path.Value, StringComparer.Ordinal)
            .ToArray();

        var documents = new List<QuerySitemapDocument>(normalized.Length);
        foreach (var group in normalized.GroupBy(
                     source => new RouteGroupIdentity(
                         source.CatalogKey.Value,
                         source.RouteKind,
                         source.RouteGroupKey)))
        {
            var routes = group.ToArray();
            if (routes
                .GroupBy(source => source.Locale.Value, StringComparer.Ordinal)
                .Any(localeGroup => localeGroup.Count() != 1))
            {
                throw Failure(
                    "QUERY_SITEMAP_SOURCE_LOCALE_DUPLICATE",
                    $"SEO route group '{group.Key.RouteGroupKey}' has multiple routes for one locale.");
            }

            if (routes
                .GroupBy(source => source.Path.Value, StringComparer.Ordinal)
                .Any(pathGroup => pathGroup.Count() != 1))
            {
                throw Failure(
                    "QUERY_SITEMAP_SOURCE_PATH_DUPLICATE",
                    $"SEO route group '{group.Key.RouteGroupKey}' assigns one path to multiple locales.");
            }

            var hreflang = routes
                .Select(source => QueryHreflangRoute.Create(
                    source.Locale.Value,
                    source.Path.Value))
                .ToArray();
            documents.AddRange(routes.Select(source =>
                QuerySitemapDocument.CreateIndexable(
                    source.RouteKind,
                    source.CatalogKey.Value,
                    source.Locale.Value,
                    source.Path.Value,
                    source.Path.Value,
                    hreflang,
                    source.LastModifiedAtUtc,
                    source.IsDraft,
                    redirectsToAnotherRoute: source.RedirectTargetPath is not null,
                    source.IsSuppressed)));
        }

        return documents
            .OrderBy(document => document.CatalogKey.Value, StringComparer.Ordinal)
            .ThenBy(document => document.Locale.Value, StringComparer.Ordinal)
            .ThenBy(document => document.Path.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static NormalizedRouteSource Normalize(PublicSeoRouteSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(source.RouteKind))
        {
            throw Failure(
                "QUERY_SITEMAP_SOURCE_KIND_INVALID",
                $"SEO source route kind '{source.RouteKind}' is unsupported.");
        }

        if (string.IsNullOrWhiteSpace(source.RouteGroupKey) ||
            source.RouteGroupKey.Length > 300 ||
            source.RouteGroupKey.Any(char.IsControl) ||
            !string.Equals(
                source.RouteGroupKey,
                source.RouteGroupKey.Trim(),
                StringComparison.Ordinal))
        {
            throw Failure(
                "QUERY_SITEMAP_SOURCE_GROUP_INVALID",
                "SEO source route group key is invalid.");
        }

        if (source.LastModifiedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "QUERY_SITEMAP_SOURCE_TIME_NOT_UTC",
                "SEO source last-modified timestamp must be UTC.");
        }

        if (source.RedirectTargetPath is not null)
        {
            _ = QuerySeoPath.CreateIndexable(
                source.RedirectTargetPath,
                nameof(source.RedirectTargetPath));
        }

        return new NormalizedRouteSource(
            source.RouteKind,
            source.RouteGroupKey,
            QuerySeoCatalogKey.Create(source.CatalogKey),
            QuerySeoLocale.Create(source.Locale),
            QuerySeoPath.CreateIndexable(source.Path),
            source.LastModifiedAtUtc,
            source.IsDraft,
            source.RedirectTargetPath,
            source.IsSuppressed);
    }

    private static QuerySitemapProjectionException Failure(string code, string detail) =>
        new(
            code,
            detail,
            "Correct the exact Query route source group and rebuild its sitemap revision.");

    private sealed record RouteGroupIdentity(
        string CatalogKey,
        QuerySeoRouteKind RouteKind,
        string RouteGroupKey);

    private sealed record NormalizedRouteSource(
        QuerySeoRouteKind RouteKind,
        string RouteGroupKey,
        QuerySeoCatalogKey CatalogKey,
        QuerySeoLocale Locale,
        QuerySeoPath Path,
        DateTimeOffset LastModifiedAtUtc,
        bool IsDraft,
        string? RedirectTargetPath,
        bool IsSuppressed);
}
