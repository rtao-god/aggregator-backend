using System.Security.Cryptography;
using System.Text;
using Aggregator.Promotion.Contracts;
using Aggregator.Query.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Query.Api;

[ApiController]
[EnableRateLimiting("public-query")]
[Route("api/catalog-query/catalogs/{catalogKey}/sponsored")]
public sealed class SponsoredListingsController(
    IPublicSponsoredListingStore store) : ControllerBase
{
    private static readonly HashSet<string> AllowedQueryKeys =
        new(["publicReadRevisionId", "locale"], StringComparer.OrdinalIgnoreCase);

    [HttpGet(Name = "GetSponsoredListings")]
    [ProducesResponseType<SponsoredListingSearchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetAsync(
        string catalogKey,
        [FromQuery] Guid publicReadRevisionId,
        [FromQuery] string locale,
        CancellationToken cancellationToken)
    {
        RejectUnknownQueryKeys();
        if (string.IsNullOrWhiteSpace(catalogKey) || catalogKey.Trim().Length > 96)
        {
            return BadRequest(Failure(
                "QUERY_SPONSORED_CATALOG_INVALID",
                "Catalog key is required and must not exceed 96 characters.",
                "Submit the exact catalog key returned by Query."));
        }

        if (publicReadRevisionId == Guid.Empty)
        {
            return BadRequest(Failure(
                "QUERY_SPONSORED_REVISION_INVALID",
                "Public read revision ID is required.",
                "Submit the exact revision rendered with the organic result."));
        }

        if (string.IsNullOrWhiteSpace(locale) || locale.Trim().Length > 35)
        {
            return BadRequest(Failure(
                "QUERY_SPONSORED_LOCALE_INVALID",
                "Locale is required and must not exceed 35 characters.",
                "Submit the exact locale used for the organic result."));
        }

        SponsoredListingSearchResponse response;
        try
        {
            response = await store.ReadAsync(
                catalogKey.Trim(),
                publicReadRevisionId,
                locale.Trim(),
                cancellationToken)
                ?? new SponsoredListingSearchResponse(
                    OverlayId: null,
                    publicReadRevisionId,
                    Sponsored: []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new QueryReadException(
                "Query.PromotionPublicRead",
                "QUERY_SPONSORED_STORE_UNAVAILABLE",
                StatusCodes.Status503ServiceUnavailable,
                "Sponsored listing projection is unavailable.",
                "Restore the Query Promotion projection and retry the exact public read revision.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalogKey"] = catalogKey.Trim(),
                    ["publicReadRevisionId"] = publicReadRevisionId,
                },
                exception);
        }

        var etag = ComputeEtag(
            publicReadRevisionId,
            response.OverlayId,
            locale.Trim());
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=30, stale-while-revalidate=120";
        Response.Headers["X-Public-Read-Revision-Id"] = publicReadRevisionId.ToString("D");
        if (response.OverlayId is { } overlayId)
        {
            Response.Headers["X-Promotion-Overlay-Id"] = overlayId.ToString("D");
        }

        if (Request.Headers.TryGetValue("If-None-Match", out var values) &&
            values.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(response);
    }

    private void RejectUnknownQueryKeys()
    {
        var unknown = Request.Query.Keys
            .Where(key => !AllowedQueryKeys.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length == 0)
        {
            return;
        }

        throw new QueryReadException(
            "Query.PromotionPublicRead",
            "QUERY_FILTER_UNSUPPORTED",
            StatusCodes.Status400BadRequest,
            $"Unsupported sponsored query parameters: {string.Join(", ", unknown)}.",
            "Remove parameters that are not declared by the sponsored Query contract.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["unknownParameters"] = unknown,
                ["allowedParameters"] = AllowedQueryKeys.Order(StringComparer.Ordinal).ToArray(),
            });
    }

    private static string ComputeEtag(
        Guid publicReadRevisionId,
        Guid? overlayId,
        string locale)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"{publicReadRevisionId:N}\n{overlayId?.ToString("N") ?? "absent"}\n{locale}");
        return $"\"{Convert.ToHexStringLower(SHA256.HashData(bytes))}\"";
    }

    private static object Failure(string code, string detail, string requiredAction) => new
    {
        type = $"https://errors.example/query/{code.Replace('_', '-')}",
        title = detail,
        status = StatusCodes.Status400BadRequest,
        owner = "Query.PromotionPublicRead",
        code,
        requiredAction,
    };
}
