using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

/// <summary>Exact keyset position inside one immutable sitemap revision.</summary>
public sealed record PublicSitemapCursor(
    Guid PublicReadRevisionId,
    QuerySeoCatalogKey CatalogKey,
    QuerySeoLocale? RequestedLocale,
    QuerySeoLocale LastLocale,
    QuerySeoPath LastPath);

/// <summary>Canonical opaque cursor codec for public sitemap pagination.</summary>
public static class PublicSitemapCursorCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Encode(PublicSitemapCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ValidateRevision(cursor.PublicReadRevisionId);
        var payload = new CursorPayload(
            cursor.PublicReadRevisionId,
            cursor.CatalogKey.Value,
            cursor.RequestedLocale?.Value,
            cursor.LastLocale.Value,
            cursor.LastPath.Value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static PublicSitemapCursor Decode(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded) ||
            encoded.Length > 4096 ||
            encoded.Any(char.IsControl) ||
            !string.Equals(encoded, encoded.Trim(), StringComparison.Ordinal))
        {
            throw InvalidCursor("Sitemap cursor is empty or exceeds its bounded wire contract.");
        }

        try
        {
            var normalized = encoded.Replace('-', '+').Replace('_', '/');
            var padding = normalized.Length % 4;
            if (padding != 0)
            {
                normalized = normalized.PadRight(normalized.Length + 4 - padding, '=');
            }

            var payload = JsonSerializer.Deserialize<CursorPayload>(
                Convert.FromBase64String(normalized),
                JsonOptions) ?? throw InvalidCursor("Sitemap cursor payload is missing.");
            ValidateRevision(payload.PublicReadRevisionId);
            return new PublicSitemapCursor(
                payload.PublicReadRevisionId,
                QuerySeoCatalogKey.Create(payload.CatalogKey, nameof(payload.CatalogKey)),
                payload.Locale is null
                    ? null
                    : QuerySeoLocale.Create(payload.Locale, nameof(payload.Locale)),
                QuerySeoLocale.Create(payload.LastLocale, nameof(payload.LastLocale)),
                QuerySeoPath.CreateIndexable(payload.LastPath, nameof(payload.LastPath)));
        }
        catch (QueryDomainException exception)
        {
            throw InvalidCursor("Sitemap cursor contains invalid Query-owned values.", exception);
        }
        catch (FormatException exception)
        {
            throw InvalidCursor("Sitemap cursor is not valid base64url.", exception);
        }
        catch (JsonException exception)
        {
            throw InvalidCursor("Sitemap cursor JSON does not match the exact contract.", exception);
        }
        catch (ArgumentException exception) when (
            string.Equals(exception.ParamName, "cursor", StringComparison.Ordinal))
        {
            throw;
        }
    }

    public static void EnsureScope(
        PublicSitemapCursor cursor,
        QuerySeoCatalogKey catalogKey,
        QuerySeoLocale? requestedLocale)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(catalogKey);
        if (!string.Equals(
                cursor.CatalogKey.Value,
                catalogKey.Value,
                StringComparison.Ordinal) ||
            !string.Equals(
                cursor.RequestedLocale?.Value,
                requestedLocale?.Value,
                StringComparison.Ordinal))
        {
            throw InvalidCursor(
                "Sitemap cursor belongs to another Catalog or locale scope.");
        }
    }

    private static void ValidateRevision(Guid publicReadRevisionId)
    {
        if (publicReadRevisionId == Guid.Empty)
        {
            throw InvalidCursor(
                "Sitemap cursor must identify an exact public-read revision.");
        }
    }

    private static ArgumentException InvalidCursor(
        string detail,
        Exception? innerException = null) =>
        new(detail, "cursor", innerException);

    private sealed record CursorPayload(
        Guid PublicReadRevisionId,
        string CatalogKey,
        string? Locale,
        string LastLocale,
        string LastPath);
}
