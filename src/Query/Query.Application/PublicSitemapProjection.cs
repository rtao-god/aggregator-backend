using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

/// <summary>Immutable Query-owned SEO revision ready for atomic persistence and activation.</summary>
public sealed record PublicSitemapProjectionArtifact(
    Guid PublicReadRevisionId,
    Guid? ExpectedCurrentPublicReadRevisionId,
    QuerySeoCatalogKey CatalogKey,
    IReadOnlyList<QuerySitemapDocument> Records,
    string ContentDigest,
    DateTimeOffset BuiltAtUtc)
{
    private IReadOnlyList<QueryRouteRedirectDocument> redirects =
        Array.Empty<QueryRouteRedirectDocument>();

    public IReadOnlyList<QueryRouteRedirectDocument> Redirects
    {
        get => redirects;
        init => redirects = value ?? throw new ArgumentNullException(nameof(value));
    }
}

public enum PublicSitemapProjectionDisposition
{
    Applied = 1,
    Duplicate = 2,
}

public sealed record PublicSitemapProjectionResult(
    Guid PublicReadRevisionId,
    PublicSitemapProjectionDisposition Disposition);

/// <summary>Atomic immutable-revision and active-pointer persistence boundary.</summary>
public interface IPublicSitemapProjectionStore
{
    Task<PublicSitemapProjectionResult> ActivateAsync(
        PublicSitemapProjectionArtifact artifact,
        CancellationToken cancellationToken);
}

/// <summary>Single pure owner for validating and digesting one complete Query SEO revision.</summary>
public static class PublicSitemapProjectionArtifactBuilder
{
    public static PublicSitemapProjectionArtifact Build(
        Guid publicReadRevisionId,
        Guid? expectedCurrentPublicReadRevisionId,
        string catalogKey,
        IReadOnlyCollection<QuerySitemapDocument> records,
        IReadOnlyCollection<QueryRouteRedirectDocument> redirects,
        DateTimeOffset builtAtUtc)
    {
        if (publicReadRevisionId == Guid.Empty)
        {
            throw Failure(
                "QUERY_SITEMAP_REVISION_ID_INVALID",
                "SEO projection requires an exact public-read revision identity.");
        }

        if (expectedCurrentPublicReadRevisionId == Guid.Empty)
        {
            throw Failure(
                "QUERY_SITEMAP_EXPECTED_REVISION_INVALID",
                "Expected active SEO revision cannot be the empty identity.");
        }

        if (builtAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "QUERY_SITEMAP_BUILD_TIME_NOT_UTC",
                "SEO projection build timestamp must be UTC.");
        }

        var normalizedCatalogKey = QuerySeoCatalogKey.Create(catalogKey);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(redirects);
        var orderedRecords = CopyAndOrderRecords(records);
        var orderedRedirects = CopyAndOrderRedirects(redirects);
        ValidateRecords(normalizedCatalogKey, orderedRecords, builtAtUtc);
        ValidateRedirects(
            normalizedCatalogKey,
            orderedRecords,
            orderedRedirects,
            builtAtUtc);
        var digest = ComputeDigest(
            publicReadRevisionId,
            normalizedCatalogKey,
            orderedRecords,
            orderedRedirects);
        return new PublicSitemapProjectionArtifact(
            publicReadRevisionId,
            expectedCurrentPublicReadRevisionId,
            normalizedCatalogKey,
            orderedRecords,
            digest,
            builtAtUtc)
        {
            Redirects = orderedRedirects,
        };
    }

    private static QuerySitemapDocument[] CopyAndOrderRecords(
        IReadOnlyCollection<QuerySitemapDocument> records)
    {
        var copy = new List<QuerySitemapDocument>(records.Count);
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            copy.Add(record);
        }

        return copy
            .OrderBy(record => record.Locale.Value, StringComparer.Ordinal)
            .ThenBy(record => record.Path.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static QueryRouteRedirectDocument[] CopyAndOrderRedirects(
        IReadOnlyCollection<QueryRouteRedirectDocument> redirects)
    {
        var copy = new List<QueryRouteRedirectDocument>(redirects.Count);
        foreach (var redirect in redirects)
        {
            ArgumentNullException.ThrowIfNull(redirect);
            copy.Add(redirect);
        }

        return copy
            .OrderBy(redirect => redirect.Locale.Value, StringComparer.Ordinal)
            .ThenBy(redirect => redirect.SourcePath.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateRecords(
        QuerySeoCatalogKey catalogKey,
        IReadOnlyList<QuerySitemapDocument> records,
        DateTimeOffset builtAtUtc)
    {
        foreach (var record in records)
        {
            if (!string.Equals(
                    record.CatalogKey.Value,
                    catalogKey.Value,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    "QUERY_SITEMAP_CATALOG_MISMATCH",
                    "Every sitemap record must belong to the exact projection Catalog.");
            }

            EnsureLocalePath(record.Locale, record.Path, "sitemap route");
            if (record.LastModifiedAtUtc > builtAtUtc)
            {
                throw Failure(
                    "QUERY_SITEMAP_LAST_MODIFIED_IN_FUTURE",
                    "Sitemap record last-modified time cannot be after the projection build time.");
            }
        }

        if (records
            .GroupBy(
                record => (record.Locale.Value, record.Path.Value),
                EqualityComparer<(string, string)>.Default)
            .Any(group => group.Count() != 1))
        {
            throw Failure(
                "QUERY_SITEMAP_ROUTE_DUPLICATE",
                "An SEO revision cannot contain duplicate locale/path route identities.");
        }

        if (records
            .GroupBy(record => record.Path.Value, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            throw Failure(
                "QUERY_SITEMAP_PATH_DUPLICATE",
                "An SEO revision cannot assign one exact path to multiple routes.");
        }

        var byRoute = records.ToDictionary(
            record => (record.Locale.Value, record.Path.Value),
            EqualityComparer<(string, string)>.Default);
        foreach (var record in records)
        {
            foreach (var alternate in record.Hreflang)
            {
                var targetIdentity = (alternate.Locale.Value, alternate.Path.Value);
                if (!byRoute.TryGetValue(targetIdentity, out var target))
                {
                    throw Failure(
                        "QUERY_SITEMAP_HREFLANG_TARGET_MISSING",
                        $"Hreflang target '{targetIdentity.Item1}:{targetIdentity.Item2}' is absent from the exact revision.");
                }

                var hasReverse = target.Hreflang.Any(candidate =>
                    string.Equals(
                        candidate.Locale.Value,
                        record.Locale.Value,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        candidate.Path.Value,
                        record.Path.Value,
                        StringComparison.Ordinal));
                if (!hasReverse)
                {
                    throw Failure(
                        "QUERY_SITEMAP_HREFLANG_NOT_RECIPROCAL",
                        "Every sitemap hreflang edge must have an exact reverse edge in the same revision.");
                }
            }
        }
    }

    private static void ValidateRedirects(
        QuerySeoCatalogKey catalogKey,
        IReadOnlyList<QuerySitemapDocument> records,
        IReadOnlyList<QueryRouteRedirectDocument> redirects,
        DateTimeOffset builtAtUtc)
    {
        var canonicalRoutes = records.ToDictionary(
            record => (record.Locale.Value, record.Path.Value),
            EqualityComparer<(string, string)>.Default);
        var canonicalPaths = records
            .Select(record => record.Path.Value)
            .ToHashSet(StringComparer.Ordinal);
        var sourceRoutes = new HashSet<(string Locale, string Path)>();
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var redirect in redirects)
        {
            if (!string.Equals(
                    redirect.CatalogKey.Value,
                    catalogKey.Value,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_CATALOG_MISMATCH",
                    "Every permanent redirect must belong to the exact projection Catalog.");
            }

            EnsureLocalePath(redirect.Locale, redirect.SourcePath, "redirect source");
            EnsureLocalePath(redirect.Locale, redirect.TargetPath, "redirect target");
            if (redirect.CreatedAtUtc > builtAtUtc)
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_CREATED_IN_FUTURE",
                    "Permanent redirect creation time cannot be after the projection build time.");
            }

            if (!sourceRoutes.Add((redirect.Locale.Value, redirect.SourcePath.Value)) ||
                !sourcePaths.Add(redirect.SourcePath.Value))
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_SOURCE_DUPLICATE",
                    "An SEO revision cannot contain duplicate permanent-redirect sources.");
            }

            if (canonicalPaths.Contains(redirect.SourcePath.Value))
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_SOURCE_IS_CANONICAL",
                    "A permanent-redirect source cannot also be a current canonical route.");
            }

            if (!canonicalRoutes.ContainsKey((
                    redirect.Locale.Value,
                    redirect.TargetPath.Value)))
            {
                throw Failure(
                    "QUERY_SEO_REDIRECT_TARGET_MISSING",
                    "Every permanent redirect must target an exact canonical route in the same revision.");
            }
        }
    }

    private static void EnsureLocalePath(
        QuerySeoLocale locale,
        QuerySeoPath path,
        string owner)
    {
        if (!path.Value.StartsWith($"/{locale.Value}/", StringComparison.Ordinal))
        {
            throw Failure(
                "QUERY_SEO_ROUTE_LOCALE_MISMATCH",
                $"The {owner} path does not belong to locale '{locale.Value}'.");
        }
    }

    private static string ComputeDigest(
        Guid publicReadRevisionId,
        QuerySeoCatalogKey catalogKey,
        IReadOnlyList<QuerySitemapDocument> records,
        IReadOnlyList<QueryRouteRedirectDocument> redirects)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("publicReadRevisionId", publicReadRevisionId.ToString("D"));
            writer.WriteString("catalogKey", catalogKey.Value);
            writer.WritePropertyName("records");
            writer.WriteStartArray();
            foreach (var record in records)
            {
                writer.WriteStartObject();
                writer.WriteNumber("routeKind", (int)record.RouteKind);
                writer.WriteString("locale", record.Locale.Value);
                writer.WriteString("path", record.Path.Value);
                writer.WriteString("canonicalPath", record.CanonicalPath.Value);
                writer.WriteString(
                    "lastModifiedAtUtc",
                    record.LastModifiedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                writer.WritePropertyName("hreflang");
                writer.WriteStartArray();
                foreach (var alternate in record.Hreflang
                             .OrderBy(item => item.Locale.Value, StringComparer.Ordinal)
                             .ThenBy(item => item.Path.Value, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("locale", alternate.Locale.Value);
                    writer.WriteString("path", alternate.Path.Value);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("redirects");
            writer.WriteStartArray();
            foreach (var redirect in redirects)
            {
                writer.WriteStartObject();
                writer.WriteString("locale", redirect.Locale.Value);
                writer.WriteString("sourcePath", redirect.SourcePath.Value);
                writer.WriteString("targetPath", redirect.TargetPath.Value);
                writer.WriteString(
                    "sourcePublicationId",
                    redirect.SourcePublicationId.ToString("D"));
                writer.WriteString("reason", redirect.Reason);
                writer.WriteString(
                    "createdAtUtc",
                    redirect.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static QuerySitemapProjectionException Failure(string code, string detail) =>
        new(
            code,
            detail,
            "Rebuild the exact public-read revision after correcting its Query-owned SEO route set.");
}

/// <summary>Single application owner for SEO validation, digesting and activation.</summary>
public sealed class BuildPublicSitemapProjectionService(IPublicSitemapProjectionStore store)
{
    public Task<PublicSitemapProjectionResult> BuildAndActivateAsync(
        Guid publicReadRevisionId,
        string catalogKey,
        IReadOnlyCollection<QuerySitemapDocument> records,
        DateTimeOffset builtAtUtc,
        CancellationToken cancellationToken) =>
        BuildAndActivateAsync(
            publicReadRevisionId,
            expectedCurrentPublicReadRevisionId: null,
            catalogKey,
            records,
            Array.Empty<QueryRouteRedirectDocument>(),
            builtAtUtc,
            cancellationToken);

    public Task<PublicSitemapProjectionResult> BuildAndActivateAsync(
        Guid publicReadRevisionId,
        Guid? expectedCurrentPublicReadRevisionId,
        string catalogKey,
        IReadOnlyCollection<QuerySitemapDocument> records,
        DateTimeOffset builtAtUtc,
        CancellationToken cancellationToken) =>
        BuildAndActivateAsync(
            publicReadRevisionId,
            expectedCurrentPublicReadRevisionId,
            catalogKey,
            records,
            Array.Empty<QueryRouteRedirectDocument>(),
            builtAtUtc,
            cancellationToken);

    public Task<PublicSitemapProjectionResult> BuildAndActivateAsync(
        Guid publicReadRevisionId,
        Guid? expectedCurrentPublicReadRevisionId,
        string catalogKey,
        IReadOnlyCollection<QuerySitemapDocument> records,
        IReadOnlyCollection<QueryRouteRedirectDocument> redirects,
        DateTimeOffset builtAtUtc,
        CancellationToken cancellationToken) =>
        store.ActivateAsync(
            PublicSitemapProjectionArtifactBuilder.Build(
                publicReadRevisionId,
                expectedCurrentPublicReadRevisionId,
                catalogKey,
                records,
                redirects,
                builtAtUtc),
            cancellationToken);
}

public sealed class QuerySitemapProjectionException : Exception
{
    public QuerySitemapProjectionException(
        string code,
        string detail,
        string requiredAction,
        Exception? innerException = null)
        : base(detail, innerException)
    {
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("Failure code is required.", nameof(code))
            : code;
        RequiredAction = string.IsNullOrWhiteSpace(requiredAction)
            ? throw new ArgumentException("Required action is required.", nameof(requiredAction))
            : requiredAction;
    }

    public string Owner => "Query.SitemapProjection";

    public string Code { get; }

    public string RequiredAction { get; }
}
