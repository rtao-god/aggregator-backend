using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

/// <summary>Validated SEO documents produced from one exact Catalog route snapshot.</summary>
public sealed record PublicSeoProjectionDocuments(
    IReadOnlyList<QuerySitemapDocument> SitemapRecords,
    IReadOnlyList<QueryRouteRedirectDocument> Redirects);

/// <summary>
/// Single Query owner that splits current indexable routes from permanent redirects and validates
/// the complete graph before either document set can be persisted.
/// </summary>
public static class PublicSeoProjectionDocumentBuilder
{
    public static PublicSeoProjectionDocuments Build(
        IReadOnlyCollection<PublicSeoRouteSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var sourceArray = new List<PublicSeoRouteSource>(sources.Count);
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            ValidateCommonSource(source);
            sourceArray.Add(source);
        }

        var canonicalSources = sourceArray
            .Where(source => source.RedirectTargetPath is null)
            .ToArray();
        var redirectSources = sourceArray
            .Where(source => source.RedirectTargetPath is not null)
            .ToArray();
        var sitemapRecords = PublicSitemapDocumentBuilder.Build(canonicalSources);
        var canonicalByRoute = BuildCanonicalRouteMap(canonicalSources);
        var redirects = BuildRedirects(redirectSources, canonicalByRoute);
        return new PublicSeoProjectionDocuments(sitemapRecords, redirects);
    }

    private static IReadOnlyDictionary<RouteIdentity, CanonicalRoute> BuildCanonicalRouteMap(
        IReadOnlyCollection<PublicSeoRouteSource> sources)
    {
        var routes = new Dictionary<RouteIdentity, CanonicalRoute>();
        var paths = new HashSet<(string CatalogKey, string Path)>();
        foreach (var source in sources)
        {
            if (source.RedirectSourcePublicationId is not null ||
                source.RedirectReason is not null ||
                source.RedirectCreatedAtUtc is not null)
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_METADATA_ORPHANED",
                    "A current canonical route cannot carry permanent-redirect metadata.");
            }

            var identity = RouteIdentity.Create(source);
            if (!routes.TryAdd(
                    identity,
                    new CanonicalRoute(source.RouteKind, source.RouteGroupKey)))
            {
                throw Failure(
                    "QUERY_SEO_ROUTE_DUPLICATE",
                    $"Current SEO route '{identity.Locale}:{identity.Path}' is duplicated.");
            }

            if (!paths.Add((identity.CatalogKey, identity.Path)))
            {
                throw Failure(
                    "QUERY_SEO_PATH_DUPLICATE",
                    $"Current SEO path '{identity.Path}' is assigned more than once in Catalog '{identity.CatalogKey}'.");
            }
        }

        return routes;
    }

    private static IReadOnlyList<QueryRouteRedirectDocument> BuildRedirects(
        IReadOnlyCollection<PublicSeoRouteSource> sources,
        IReadOnlyDictionary<RouteIdentity, CanonicalRoute> canonicalByRoute)
    {
        var redirectBySource = new Dictionary<RouteIdentity, RouteIdentity>();
        foreach (var source in sources)
        {
            if (source.IsDraft || source.IsSuppressed)
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_NOT_PUBLIC",
                    "Draft or safety-suppressed routes cannot enter the public redirect projection.");
            }

            var sourceIdentity = RouteIdentity.Create(source);
            var targetIdentity = RouteIdentity.Create(
                source.CatalogKey,
                source.Locale,
                source.RedirectTargetPath!);
            if (sourceIdentity == targetIdentity)
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_SELF_TARGET",
                    $"Permanent redirect source '{sourceIdentity.Path}' cannot target itself.");
            }

            if (!redirectBySource.TryAdd(sourceIdentity, targetIdentity))
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_SOURCE_DUPLICATE",
                    $"Permanent redirect source '{sourceIdentity.Locale}:{sourceIdentity.Path}' is duplicated.");
            }

            if (canonicalByRoute.ContainsKey(sourceIdentity))
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_SOURCE_IS_CANONICAL",
                    $"Permanent redirect source '{sourceIdentity.Path}' is also a current canonical route.");
            }
        }

        EnsureNoCycles(redirectBySource);
        foreach (var edge in redirectBySource)
        {
            if (redirectBySource.ContainsKey(edge.Value))
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_CHAIN_FORBIDDEN",
                    $"Permanent redirect '{edge.Key.Path}' targets another redirect source '{edge.Value.Path}'.");
            }
        }

        var redirects = new List<QueryRouteRedirectDocument>(sources.Count);
        foreach (var source in sources)
        {
            var sourceIdentity = RouteIdentity.Create(source);
            var targetIdentity = redirectBySource[sourceIdentity];
            if (!canonicalByRoute.TryGetValue(targetIdentity, out var canonicalTarget))
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_TARGET_MISSING",
                    $"Permanent redirect target '{targetIdentity.Locale}:{targetIdentity.Path}' is absent from the exact current route snapshot.");
            }

            if (canonicalTarget.RouteKind != source.RouteKind ||
                !string.Equals(
                    canonicalTarget.RouteGroupKey,
                    source.RouteGroupKey,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_TARGET_IDENTITY_MISMATCH",
                    "A permanent redirect must target the same route kind and route-group identity.");
            }

            if (source.RedirectSourcePublicationId is null ||
                source.RedirectReason is null ||
                source.RedirectCreatedAtUtc is null)
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_METADATA_REQUIRED",
                    "A permanent redirect requires publication identity, reason and creation time.");
            }

            redirects.Add(QueryRouteRedirectDocument.CreatePermanent(
                source.CatalogKey,
                source.Locale,
                source.Path,
                source.RedirectTargetPath!,
                source.RedirectSourcePublicationId.Value,
                source.RedirectReason,
                source.RedirectCreatedAtUtc.Value));
        }

        return redirects
            .OrderBy(redirect => redirect.CatalogKey.Value, StringComparer.Ordinal)
            .ThenBy(redirect => redirect.Locale.Value, StringComparer.Ordinal)
            .ThenBy(redirect => redirect.SourcePath.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static void EnsureNoCycles(
        IReadOnlyDictionary<RouteIdentity, RouteIdentity> redirectBySource)
    {
        var completed = new HashSet<RouteIdentity>();
        foreach (var start in redirectBySource.Keys)
        {
            if (completed.Contains(start))
            {
                continue;
            }

            var path = new HashSet<RouteIdentity>();
            var current = start;
            while (redirectBySource.TryGetValue(current, out var next))
            {
                if (!path.Add(current))
                {
                    throw Failure(
                        "QUERY_SEO_REDIRECT_LOOP",
                        $"Permanent redirect graph contains a loop at '{current.Locale}:{current.Path}'.");
                }

                current = next;
            }

            completed.UnionWith(path);
        }
    }

    private static void ValidateCommonSource(PublicSeoRouteSource source)
    {
        if (!Enum.IsDefined(source.RouteKind))
        {
            throw Failure(
                "QUERY_SEO_ROUTE_KIND_INVALID",
                $"SEO route kind '{source.RouteKind}' is unsupported.");
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
                "QUERY_SEO_ROUTE_GROUP_INVALID",
                "SEO route group key is invalid.");
        }

        _ = QuerySeoCatalogKey.Create(source.CatalogKey);
        var locale = QuerySeoLocale.Create(source.Locale);
        var path = QuerySeoPath.CreateIndexable(source.Path);
        EnsureLocalePath(locale, path, nameof(source.Path));
        if (source.RedirectTargetPath is not null)
        {
            var target = QuerySeoPath.CreateIndexable(
                source.RedirectTargetPath,
                nameof(source.RedirectTargetPath));
            EnsureLocalePath(locale, target, nameof(source.RedirectTargetPath));
        }

        if (source.LastModifiedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "QUERY_SEO_ROUTE_TIME_NOT_UTC",
                "SEO route last-modified timestamp must be UTC.");
        }
    }

    private static void EnsureLocalePath(
        QuerySeoLocale locale,
        QuerySeoPath path,
        string parameterName)
    {
        var prefix = $"/{locale.Value}/";
        if (!path.Value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw Failure(
                "QUERY_SEO_ROUTE_LOCALE_MISMATCH",
                $"SEO path '{parameterName}' does not belong to locale '{locale.Value}'.");
        }
    }

    private static QuerySitemapProjectionException Failure(string code, string detail) =>
        new(
            code,
            detail,
            "Correct the exact Catalog route and redirect manifests before rebuilding Query SEO.");

    private sealed record CanonicalRoute(
        QuerySeoRouteKind RouteKind,
        string RouteGroupKey);

    private sealed record RouteIdentity(
        string CatalogKey,
        string Locale,
        string Path)
    {
        public static RouteIdentity Create(PublicSeoRouteSource source) =>
            Create(source.CatalogKey, source.Locale, source.Path);

        public static RouteIdentity Create(
            string catalogKey,
            string locale,
            string path) =>
            new(
                QuerySeoCatalogKey.Create(catalogKey).Value,
                QuerySeoLocale.Create(locale).Value,
                QuerySeoPath.CreateIndexable(path).Value);
    }
}
