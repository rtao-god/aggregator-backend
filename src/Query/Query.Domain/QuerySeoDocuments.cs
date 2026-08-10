namespace Aggregator.Query.Domain;

/// <summary>Query-owned route kinds eligible for the public SEO projection.</summary>
public enum QuerySeoRouteKind
{
    Listing = 1,
    Category = 2,
    EditorialLanding = 3,
}

/// <summary>Validated Catalog key used by the Query SEO owner.</summary>
public sealed record QuerySeoCatalogKey
{
    private QuerySeoCatalogKey(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static QuerySeoCatalogKey Create(string value, string parameterName = "catalogKey")
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            value.Any(char.IsControl) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_KEY_INVALID",
                $"SEO key '{parameterName}' is invalid.");
        }

        return new QuerySeoCatalogKey(value);
    }

    public override string ToString() => Value;
}

/// <summary>Validated authored locale identity used by SEO and sitemap projections.</summary>
public sealed record QuerySeoLocale
{
    private QuerySeoLocale(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static QuerySeoLocale Create(string value, string parameterName = "locale")
    {
        if (value is null ||
            value.Length != 5 ||
            value[2] != '-' ||
            !char.IsAsciiLetterLower(value[0]) ||
            !char.IsAsciiLetterLower(value[1]) ||
            !char.IsAsciiLetterUpper(value[3]) ||
            !char.IsAsciiLetterUpper(value[4]))
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_LOCALE_INVALID",
                $"SEO locale '{parameterName}' must use the exact language-REGION form.");
        }

        return new QuerySeoLocale(value);
    }

    public override string ToString() => Value;
}

/// <summary>Validated path that can be emitted as a canonical or sitemap URL path.</summary>
public sealed record QuerySeoPath
{
    private QuerySeoPath(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static QuerySeoPath CreateIndexable(string value, string parameterName = "path")
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 2048 ||
            !value.StartsWith('/', StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            value.Contains('?') ||
            value.Contains('#') ||
            value.Any(char.IsControl) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_PATH_INVALID",
                $"SEO path '{parameterName}' is not an indexable route path.");
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..") ||
            value.Contains("//", StringComparison.Ordinal))
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_PATH_INVALID",
                $"SEO path '{parameterName}' contains a non-canonical segment.");
        }

        return new QuerySeoPath(value);
    }

    public override string ToString() => Value;
}

/// <summary>One exact locale/path member of an hreflang group.</summary>
public sealed record QueryHreflangRoute
{
    private QueryHreflangRoute(QuerySeoLocale locale, QuerySeoPath path)
    {
        Locale = locale;
        Path = path;
    }

    public QuerySeoLocale Locale { get; }

    public QuerySeoPath Path { get; }

    public static QueryHreflangRoute Create(string locale, string path) =>
        new(
            QuerySeoLocale.Create(locale),
            QuerySeoPath.CreateIndexable(path));
}

/// <summary>
/// One active, indexable sitemap document. Draft, redirecting and suppressed routes are rejected
/// before they can enter the Query SEO read model.
/// </summary>
public sealed record QuerySitemapDocument
{
    private QuerySitemapDocument(
        QuerySeoRouteKind routeKind,
        QuerySeoCatalogKey catalogKey,
        QuerySeoLocale locale,
        QuerySeoPath path,
        QuerySeoPath canonicalPath,
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

    public QuerySeoCatalogKey CatalogKey { get; }

    public QuerySeoLocale Locale { get; }

    public QuerySeoPath Path { get; }

    public QuerySeoPath CanonicalPath { get; }

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
            throw QuerySeoFailure.Create(
                "QUERY_SEO_ROUTE_KIND_INVALID",
                $"SEO route kind '{routeKind}' is unsupported.");
        }

        if (isDraft)
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_DRAFT_NOT_INDEXABLE",
                "Draft routes cannot enter the public sitemap projection.");
        }

        if (redirectsToAnotherRoute)
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_REDIRECT_NOT_INDEXABLE",
                "Redirecting routes cannot enter the public sitemap projection.");
        }

        if (isSuppressed)
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_SUPPRESSED_NOT_INDEXABLE",
                "Safety-suppressed routes cannot enter the public sitemap projection.");
        }

        var normalizedCatalogKey = QuerySeoCatalogKey.Create(catalogKey);
        var normalizedLocale = QuerySeoLocale.Create(locale);
        var normalizedPath = QuerySeoPath.CreateIndexable(path);
        var normalizedCanonicalPath = QuerySeoPath.CreateIndexable(
            canonicalPath,
            nameof(canonicalPath));
        if (!string.Equals(
                normalizedPath.Value,
                normalizedCanonicalPath.Value,
                StringComparison.Ordinal))
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_CANONICAL_NOT_SELF",
                "An indexable sitemap route must use itself as the canonical path.");
        }

        ArgumentNullException.ThrowIfNull(hreflang);
        if (hreflang.Count == 0)
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_HREFLANG_REQUIRED",
                "An indexable sitemap route must belong to an explicit hreflang group.");
        }

        var orderedHreflang = hreflang
            .OrderBy(item => item.Locale.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Path.Value, StringComparer.Ordinal)
            .ToArray();
        if (orderedHreflang
            .GroupBy(item => item.Locale.Value, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_HREFLANG_LOCALE_DUPLICATE",
                "An hreflang group cannot contain multiple routes for the same locale.");
        }

        if (orderedHreflang
            .GroupBy(item => item.Path.Value, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_HREFLANG_PATH_DUPLICATE",
                "An hreflang group cannot assign one route path to multiple locales.");
        }

        if (!orderedHreflang.Any(item =>
                string.Equals(item.Locale.Value, normalizedLocale.Value, StringComparison.Ordinal) &&
                string.Equals(item.Path.Value, normalizedPath.Value, StringComparison.Ordinal)))
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_HREFLANG_SELF_MISSING",
                "An indexable route must include its exact locale/path identity in hreflang.");
        }

        if (lastModifiedAtUtc.Offset != TimeSpan.Zero)
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_TIME_NOT_UTC",
                "SEO last-modified timestamp must be UTC.");
        }

        return new QuerySitemapDocument(
            routeKind,
            normalizedCatalogKey,
            normalizedLocale,
            normalizedPath,
            normalizedCanonicalPath,
            orderedHreflang,
            lastModifiedAtUtc);
    }
}

internal static class QuerySeoFailure
{
    public static QueryDomainException Create(string code, string detail) =>
        new(code, detail);
}
