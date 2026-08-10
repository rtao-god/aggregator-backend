using Aggregator.Query.Contracts;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

/// <summary>Validated Query-owned sitemap page request.</summary>
public sealed record PublicSitemapPageRequest(
    QuerySeoCatalogKey CatalogKey,
    QuerySeoLocale? Locale,
    int PageSize,
    string? Cursor);

/// <summary>Infrastructure result bound to one exact active public-read revision.</summary>
public sealed record PublicSitemapSlice(
    Guid PublicReadRevisionId,
    IReadOnlyList<QuerySitemapDocument> Items,
    string? NextCursor);

/// <summary>Read-only sitemap persistence boundary owned by Query.</summary>
public interface IPublicSitemapStore
{
    Task<PublicSitemapSlice?> ReadPageAsync(
        PublicSitemapPageRequest request,
        CancellationToken cancellationToken);
}

public enum PublicSitemapReadStatus
{
    Ready = 1,
    ProjectionUnavailable = 2,
}

/// <summary>Explicit read result; unavailable projection is never represented as an empty sitemap.</summary>
public sealed record PublicSitemapReadResult(
    PublicSitemapReadStatus Status,
    PublicSitemapPageDto? Page);

/// <summary>Single application owner for public sitemap reads.</summary>
public sealed class ReadPublicSitemapService(IPublicSitemapStore store)
{
    public async Task<PublicSitemapReadResult> ReadAsync(
        string catalogKey,
        string? locale,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                "Sitemap page size must be between 1 and 1000.");
        }

        var normalizedCursor = NormalizeCursor(cursor);
        var request = new PublicSitemapPageRequest(
            QuerySeoCatalogKey.Create(catalogKey),
            locale is null ? null : QuerySeoLocale.Create(locale),
            pageSize,
            normalizedCursor);
        var slice = await store.ReadPageAsync(request, cancellationToken);
        if (slice is null)
        {
            return new PublicSitemapReadResult(
                PublicSitemapReadStatus.ProjectionUnavailable,
                Page: null);
        }

        if (slice.PublicReadRevisionId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Query sitemap store returned an empty public-read revision identity.");
        }

        ArgumentNullException.ThrowIfNull(slice.Items);
        var mappedItems = slice.Items
            .Select(Map)
            .ToArray();
        if (mappedItems.Length > pageSize)
        {
            throw new InvalidOperationException(
                "Query sitemap store returned more records than the requested page size.");
        }

        return new PublicSitemapReadResult(
            PublicSitemapReadStatus.Ready,
            new PublicSitemapPageDto(
                slice.PublicReadRevisionId,
                mappedItems,
                NormalizeCursor(slice.NextCursor)));
    }

    private static PublicSitemapRecordDto Map(QuerySitemapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new PublicSitemapRecordDto(
            document.RouteKind switch
            {
                QuerySeoRouteKind.Listing => PublicSeoRouteKindContract.Listing,
                QuerySeoRouteKind.Category => PublicSeoRouteKindContract.Category,
                QuerySeoRouteKind.EditorialLanding => PublicSeoRouteKindContract.EditorialLanding,
                _ => throw new InvalidOperationException(
                    $"Unsupported Query SEO route kind '{document.RouteKind}'."),
            },
            document.CatalogKey.Value,
            document.Locale.Value,
            document.Path.Value,
            document.CanonicalPath.Value,
            document.Hreflang
                .Select(item => new PublicHreflangLinkDto(
                    item.Locale.Value,
                    item.Path.Value))
                .ToArray(),
            document.LastModifiedAtUtc);
    }

    private static string? NormalizeCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(cursor) ||
            cursor.Length > 4096 ||
            cursor.Any(char.IsControl) ||
            !string.Equals(cursor, cursor.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Sitemap cursor is invalid.",
                nameof(cursor));
        }

        return cursor;
    }
}
