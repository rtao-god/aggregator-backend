using System.Security.Cryptography;
using System.Text;
using Aggregator.Promotion.Contracts;
using Aggregator.Query.Application;
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
            throw Failure(
                "QUERY_SPONSORED_CATALOG_INVALID",
                StatusCodes.Status400BadRequest,
                "Catalog key is required and must not exceed 96 characters.",
                "Submit the exact catalog key returned by Query.");
        }

        if (publicReadRevisionId == Guid.Empty)
        {
            throw Failure(
                "QUERY_SPONSORED_REVISION_INVALID",
                StatusCodes.Status400BadRequest,
                "Public read revision ID is required.",
                "Submit the exact revision rendered with the organic result.");
        }

        if (string.IsNullOrWhiteSpace(locale) || locale.Trim().Length > 35)
        {
            throw Failure(
                "QUERY_SPONSORED_LOCALE_INVALID",
                StatusCodes.Status400BadRequest,
                "Locale is required and must not exceed 35 characters.",
                "Submit the exact locale used for the organic result.");
        }

        var normalizedCatalogKey = catalogKey.Trim();
        var normalizedLocale = locale.Trim();
        SponsoredListingSearchResponse? response;
        try
        {
            response = await store.ReadAsync(
                normalizedCatalogKey,
                publicReadRevisionId,
                normalizedLocale,
                cancellationToken);
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
                    ["catalogKey"] = normalizedCatalogKey,
                    ["publicReadRevisionId"] = publicReadRevisionId,
                },
                exception);
        }

        if (response is null)
        {
            throw Failure(
                "QUERY_SPONSORED_OVERLAY_UNAVAILABLE",
                StatusCodes.Status503ServiceUnavailable,
                "No sealed Promotion overlay exists for the requested public read revision.",
                "Project an explicit empty or populated Promotion overlay before serving this revision.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalogKey"] = normalizedCatalogKey,
                    ["publicReadRevisionId"] = publicReadRevisionId,
                    ["locale"] = normalizedLocale,
                });
        }

        if (response.SourcePublicReadRevisionId != publicReadRevisionId)
        {
            throw Failure(
                "QUERY_SPONSORED_REVISION_MISMATCH",
                StatusCodes.Status503ServiceUnavailable,
                "Sponsored projection belongs to another public read revision.",
                "Rebuild the Promotion overlay for the exact requested public read revision.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["requestedPublicReadRevisionId"] = publicReadRevisionId,
                    ["actualPublicReadRevisionId"] = response.SourcePublicReadRevisionId,
                    ["overlayId"] = response.OverlayId,
                });
        }

        var etag = ComputeEtag(
            publicReadRevisionId,
            response.OverlayId,
            normalizedLocale);
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=30, stale-while-revalidate=120";
        Response.Headers["X-Public-Read-Revision-Id"] = publicReadRevisionId.ToString("D");
        Response.Headers["X-Promotion-Overlay-Id"] = response.OverlayId.ToString("D");

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

        throw Failure(
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
        Guid overlayId,
        string locale)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"{publicReadRevisionId:N}\n{overlayId:N}\n{locale}");
        return $"\"{Convert.ToHexStringLower(SHA256.HashData(bytes))}\"";
    }

    private static QueryReadException Failure(
        string code,
        int statusCode,
        string detail,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null) =>
        new(
            "Query.PromotionPublicRead",
            code,
            statusCode,
            detail,
            requiredAction,
            context ?? new Dictionary<string, object?>(StringComparer.Ordinal));
}
