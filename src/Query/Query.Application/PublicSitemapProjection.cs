using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

/// <summary>Immutable Query-owned sitemap revision ready for atomic persistence and activation.</summary>
public sealed record PublicSitemapProjectionArtifact(
    Guid PublicReadRevisionId,
    QuerySeoCatalogKey CatalogKey,
    IReadOnlyList<QuerySitemapDocument> Records,
    string ContentDigest,
    DateTimeOffset BuiltAtUtc);

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

/// <summary>Single application owner for sitemap validation, digesting and activation.</summary>
public sealed class BuildPublicSitemapProjectionService(IPublicSitemapProjectionStore store)
{
    public Task<PublicSitemapProjectionResult> BuildAndActivateAsync(
        Guid publicReadRevisionId,
        string catalogKey,
        IReadOnlyCollection<QuerySitemapDocument> records,
        DateTimeOffset builtAtUtc,
        CancellationToken cancellationToken)
    {
        if (publicReadRevisionId == Guid.Empty)
        {
            throw Failure(
                "QUERY_SITEMAP_REVISION_ID_INVALID",
                "Sitemap projection requires an exact public-read revision identity.");
        }

        if (builtAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "QUERY_SITEMAP_BUILD_TIME_NOT_UTC",
                "Sitemap projection build timestamp must be UTC.");
        }

        var normalizedCatalogKey = QuerySeoCatalogKey.Create(catalogKey);
        ArgumentNullException.ThrowIfNull(records);
        var orderedRecords = records
            .OrderBy(record => record.Locale.Value, StringComparer.Ordinal)
            .ThenBy(record => record.Path.Value, StringComparer.Ordinal)
            .ToArray();
        ValidateRecords(normalizedCatalogKey, orderedRecords, builtAtUtc);
        var digest = ComputeDigest(
            publicReadRevisionId,
            normalizedCatalogKey,
            orderedRecords);
        return store.ActivateAsync(
            new PublicSitemapProjectionArtifact(
                publicReadRevisionId,
                normalizedCatalogKey,
                orderedRecords,
                digest,
                builtAtUtc),
            cancellationToken);
    }

    private static void ValidateRecords(
        QuerySeoCatalogKey catalogKey,
        IReadOnlyList<QuerySitemapDocument> records,
        DateTimeOffset builtAtUtc)
    {
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (!string.Equals(
                    record.CatalogKey.Value,
                    catalogKey.Value,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    "QUERY_SITEMAP_CATALOG_MISMATCH",
                    "Every sitemap record must belong to the exact projection Catalog.");
            }

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
                "A sitemap revision cannot contain duplicate locale/path route identities.");
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
                        $"Hreflang target '{targetIdentity.Value}:{targetIdentity.Value}' is absent from the exact revision.");
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

    private static string ComputeDigest(
        Guid publicReadRevisionId,
        QuerySeoCatalogKey catalogKey,
        IReadOnlyList<QuerySitemapDocument> records)
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

public sealed class QuerySitemapProjectionException : Exception
{
    public QuerySitemapProjectionException(
        string code,
        string detail,
        string requiredAction)
        : base(detail)
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
