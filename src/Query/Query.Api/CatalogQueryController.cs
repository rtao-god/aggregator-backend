using System.Security.Cryptography;
using System.Text;
using Aggregator.Query.Application;
using Aggregator.Query.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Query.Api;

[ApiController]
[EnableRateLimiting("public-query")]
[Route("api/catalog-query/catalogs/{catalogKey}")]
public sealed class CatalogQueryController(PublicQueryService service) : ControllerBase
{
    private static readonly HashSet<string> SearchQueryKeys =
        new(["locale", "category", "pageSize", "cursor"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RouteQueryKeys =
        new(["locale"], StringComparer.OrdinalIgnoreCase);

    [HttpGet("listings", Name = "SearchPublicListings")]
    [ProducesResponseType<PublicListingSearchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SearchAsync(
        string catalogKey,
        [FromQuery] string locale,
        [FromQuery] string? category = null,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        RejectUnknownQueryKeys(SearchQueryKeys);
        var result = await service.SearchAsync(
            catalogKey,
            locale,
            category,
            pageSize,
            cursor,
            cancellationToken);
        return WithPublicCaching(
            result,
            result.Metadata.PublicReadRevisionId,
            $"search\n{catalogKey.Trim()}\n{locale.Trim()}\n{category?.Trim()}\n{pageSize}\n{cursor}");
    }

    [HttpGet("routes/{**path}", Name = "GetPublicListingByRoute")]
    [ProducesResponseType<PublicListingCardResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetByRouteAsync(
        string catalogKey,
        string path,
        [FromQuery] string locale,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        RejectUnknownQueryKeys(RouteQueryKeys);
        var absolutePath = $"/{path.TrimStart('/')}";
        var result = await service.GetByRouteAsync(
            catalogKey,
            absolutePath,
            locale,
            cancellationToken);
        return WithPublicCaching(
            result,
            result.Metadata.PublicReadRevisionId,
            $"route\n{catalogKey.Trim()}\n{absolutePath}\n{locale.Trim()}");
    }

    private void RejectUnknownQueryKeys(IReadOnlySet<string> allowedKeys)
    {
        var unknownKeys = Request.Query.Keys
            .Where(key => !allowedKeys.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unknownKeys.Length > 0)
        {
            throw new QueryReadException(
                "Query.PublicApi",
                "QUERY_FILTER_UNSUPPORTED",
                StatusCodes.Status400BadRequest,
                $"Unsupported query parameters: {string.Join(", ", unknownKeys)}.",
                "Remove parameters that are not declared by the Query API contract.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["unknownParameters"] = unknownKeys,
                    ["allowedParameters"] = allowedKeys.Order(StringComparer.Ordinal).ToArray(),
                });
        }
    }

    private IActionResult WithPublicCaching<TResponse>(
        TResponse response,
        Guid publicReadRevisionId,
        string requestIdentity)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestIdentity);
        var digestInput = Encoding.UTF8.GetBytes($"{publicReadRevisionId:N}\n{requestIdentity}");
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(digestInput)).ToLowerInvariant()}\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=60, stale-while-revalidate=300";
        Response.Headers["X-Public-Read-Revision-Id"] = publicReadRevisionId.ToString("D");
        if (Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(response);
    }
}
