namespace Aggregator.Query.Domain;

/// <summary>Query-owned route kinds eligible for the public SEO projection.</summary>
public enum QuerySeoRouteKind
{
    Listing = 1,
    Category = 2,
    EditorialLanding = 3,
}

/// <summary>One exact locale/path member of an hreflang group.</summary>
public sealed record QueryHreflangRoute
{
    private QueryHreflangRoute(string locale, string path)
    {
        Locale = locale;
        Path = path;
    }

    public string Locale { get; }

    public string Path { get; }

    public static QueryHreflangRoute Create(string locale, string path) =>
        new(
            QuerySeoRules.RequireLocale(locale, nameof(locale)),
            QuerySeoRules.RequireIndexablePath(path, nameof(path)));
}

/// <summary>
/// One active, indexable sitemap document. Draft, redirecting and suppressed routes are rejected
/// before they can enter the Query SEO read model.
/// </summary>
public sealed record QuerySitemapDocument
{
    private QuerySitemapDocument(
        QuerySeoRouteKind routeKind,
        string catalogKey,
        string locale,
        string path,
        string canonicalPath,
        IReadOnlyList<QueryHreflangRoute> hreflang,
        DateTimeOffset lastModifiedAtUtc)
    {
        RouteKind = routeKind;
        CatalogKey = catalogKey;
        Locale = locale;
        Path = path;
        CanonicalPath = canonicalPath;
        Hreflang = hreflang;
        LastModifiedAtUtc = lastModifiedAtUtc;
    }

    public QuerySeoRouteKind RouteKind { get; }

    public string CatalogKey { get; }

    public string Locale { get; }

    public string Path { get; }

    public string CanonicalPath { get; }

    public IReadOnlyList<QueryHreflangRoute> Hreflang { get; }

    public DateTimeOffset LastModifiedAtUtc { get; }

    public static QuerySitemapDocument CreateIndexable(
        QuerySeoRouteKind routeKind,
        string catalogKey,
        string locale,
        string path,
        string canonicalPath,
        IReadOnlyCollection<QueryHreflangRoute> hreflang,
        DateTimeOffset lastModifiedAtUtc,
        bool isDraft,
        bool redirectsToAnotherRoute,
        bool isSuppressed)
    {
        if (!Enum.IsDefined(routeKind))
        {
            throw Failure(
                "QUERY_SEO_ROUTE_KIND_INVALID",
                $"SEO route kind '{routeKind}' is unsupported.");
        }

        if (isDraft)
        {
            throw Failure(
                "QUERY_SEO_DRAFT_NOT_INDEXABLE",
                "Draft routes cannot enter the public sitemap projection.");
        }

        if (redirectsToAnotherRoute)
        {
            throw Failure(
                "QUERY_SEO_REDIRECT_NOT_INDEXABLE",
                "Redirecting routes cannot enter the public sitemap projection.");
        }

        if (isSuppressed)
        {
            throw Failure(
                "QUERY_SEO_SUPPRESSED_NOT_INDEXABLE",
                "Safety-suppressed routes cannot enter the public sitemap projection.");
        }

        var normalizedCatalogKey = QuerySeoRules.RequireKey(catalogKey, nameof(catalogKey));
        var normalizedLocale = QuerySeoRules.RequireLocale(locale, nameof(locale));
        var normalizedPath = QuerySeoRules.RequireIndexablePath(path, nameof(path));
        var normalizedCanonicalPath = QuerySeoRules.RequireIndexablePath(
            canonicalPath,
            nameof(canonicalPath));
        if (!string.Equals(normalizedPath, normalizedCanonicalPath, StringComparison.Ordinal))
        {
            throw Failure(
                "QUERY_SEO_CANONICAL_NOT_SELF",
                "An indexable sitemap route must use itself as the canonical path.");
        }

        ArgumentNullException.ThrowIfNull(hreflang);
        if (hreflang.Count == 0)
        {
            throw Failure(
                "QUERY_SEO_HREFLANG_REQUIRED",
                "An indexable sitemap route must belong to an explicit hreflang group.");
        }

        var orderedHreflang = hreflang
            .OrderBy(item => item.Locale, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
        if (orderedHreflang
            .GroupBy(item => item.Locale, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            throw Failure(
                "QUERY_SEO_HREFLANG_LOCALE_DUPLICATE",
                "An hreflang group cannot contain multiple routes for the same locale.");
        }

        if (orderedHreflang
            .GroupBy(item => item.Path, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            throw Failure(
                "QUERY_SEO_HREFLANG_PATH_DUPLICATE",
                "An hreflang group cannot assign one route path to multiple locales.");
        }

        if (!orderedHreflang.Any(item =>
                string.Equals(item.Locale, normalizedLocale, StringComparison.Ordinal) &&
                string.Equals(item.Path, normalizedPath, StringComparison.Ordinal)))
        {
            throw Failure(
                "QUERY_SEO_HREFLANG_SELF_MISSING",
                "An indexable route must include its exact locale/path identity in hreflang.");
        }

        return new QuerySitemapDocument(
            routeKind,
            normalizedCatalogKey,
            normalizedLocale,
            normalizedPath,
            normalizedCanonicalPath,
            orderedHreflang,
            QuerySeoRules.RequireUtc(lastModifiedAtUtc, nameof(lastModifiedAtUtc)));
    }

    private static QueryDomainException Failure(string code, string detail) =>
        new(code, detail);
}

internal static class QuerySeoRules
{
    public static string RequireKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            value.Any(char.IsControl) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Failure(
                "QUERY_SEO_KEY_INVALID",
                $"SEO key '{parameterName}' is invalid.");
        }

        return value;
    }

    public static string RequireLocale(string value, string parameterName)
    {
        if (value is null ||
            value.Length != 5 ||
            value[2] != '-' ||
            !char.IsAsciiLetterLower(value[0]) ||
            !char.IsAsciiLetterLower(value[1]) ||
            !char.IsAsciiLetterUpper(value[3]) ||
            !char.IsAsciiLetterUpper(value[4]))
        {
            throw Failure(
                "QUERY_SEO_LOCALE_INVALID",
                $"SEO locale '{parameterName}' must use the exact language-REGION form.");
        }

        return value;
    }

    public static string RequireIndexablePath(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 2048 ||
            !value.StartsWith('/', StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            value.Contains('?', StringComparison.Ordinal) ||
            value.Contains('#', StringComparison.Ordinal) ||
            value.Any(char.IsControl) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Failure(
                "QUERY_SEO_PATH_INVALID",
                $"SEO path '{parameterName}' is not an indexable route path.");
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..") ||
            value.Contains("//", StringComparison.Ordinal))
        {
            throw Failure(
                "QUERY_SEO_PATH_INVALID",
                $"SEO path '{parameterName}' contains a non-canonical segment.");
        }

        return value;
    }

    public static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "QUERY_SEO_TIME_NOT_UTC",
                $"SEO timestamp '{parameterName}' must be UTC.");
        }

        return value;
    }

    private static QueryDomainException Failure(string code, string detail) =>
        new(code, detail);
}
