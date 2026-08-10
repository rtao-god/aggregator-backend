using Aggregator.Query.Application;
using Aggregator.Query.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Aggregator.Query.Api;

[ApiController]
[Route("api/catalog-query/catalogs/{catalogKey}")]
public sealed class CatalogQueryController(PublicQueryService service) : ControllerBase
{
    private static readonly IReadOnlySet<string> SupportedSearchParameters =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "locale",
            "category",
            "district",
            "listingKind",
            "contactKind",
            "pageSize",
            "cursor",
        };

    [HttpGet("listings", Name = "SearchPublicListings")]
    [ProducesResponseType<PublicListingSearchResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PublicListingSearchResponse>> GetListings(
        [FromRoute] string catalogKey,
        [FromQuery] string locale = "de-DE",
        [FromQuery] string? category = null,
        [FromQuery] string? district = null,
        [FromQuery] string? listingKind = null,
        [FromQuery] string? contactKind = null,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        EnsureKnownSearchParameters(Request.Query.Keys);
        var response = await service.SearchAsync(
            catalogKey,
            new PublicListingSearchRequest(
                locale,
                category,
                district,
                ParseListingKind(listingKind),
                ParseContactKind(contactKind),
                pageSize,
                cursor),
            cancellationToken);
        Response.Headers.ETag = QueryHttpCache.BuildETag(response.Metadata.PublicReadRevisionId);
        Response.Headers.CacheControl = "public,max-age=60,stale-while-revalidate=300";
        return Ok(response);
    }

    [HttpGet("routes/{**path}", Name = "GetPublicListingByRoute")]
    [ProducesResponseType<PublicListingCardResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PublicListingCardResponse>> GetByRoute(
        [FromRoute] string catalogKey,
        [FromRoute] string path,
        [FromQuery] string locale = "de-DE",
        CancellationToken cancellationToken = default)
    {
        var response = await service.GetByRouteAsync(
            catalogKey,
            $"/{path.TrimStart('/')}",
            locale,
            cancellationToken);
        Response.Headers.ETag = QueryHttpCache.BuildETag(response.Metadata.PublicReadRevisionId);
        Response.Headers.CacheControl = "public,max-age=300,stale-while-revalidate=3600";
        return Ok(response);
    }

    private static void EnsureKnownSearchParameters(IEnumerable<string> parameterNames)
    {
        var unknown = parameterNames
            .Where(item => !SupportedSearchParameters.Contains(item))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length == 0)
        {
            return;
        }

        throw new QueryReadException(
            "Query.Search",
            "QUERY_FILTER_UNKNOWN",
            400,
            $"Unknown public search parameter(s): {string.Join(", ", unknown)}.",
            "Remove parameters that are not declared by the public Query contract.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["unknownParameters"] = unknown,
            });
    }

    private static PublicListingKindContract? ParseListingKind(string? value) => value switch
    {
        null => null,
        "place" => PublicListingKindContract.Place,
        "provider" => PublicListingKindContract.Provider,
        _ => throw InvalidFilter(
            "listingKind",
            value,
            "Supported listingKind values are 'place' and 'provider'."),
    };

    private static PublicContactKindContract? ParseContactKind(string? value) => value switch
    {
        null => null,
        "website" => PublicContactKindContract.Website,
        "email" => PublicContactKindContract.Email,
        "phone" => PublicContactKindContract.Phone,
        "whatsapp" => PublicContactKindContract.WhatsApp,
        "booking_reference" => PublicContactKindContract.BookingReference,
        "map_reference" => PublicContactKindContract.MapReference,
        _ => throw InvalidFilter(
            "contactKind",
            value,
            "Supported contactKind values are 'website', 'email', 'phone', 'whatsapp', 'booking_reference', and 'map_reference'."),
    };

    private static QueryReadException InvalidFilter(
        string parameterName,
        string value,
        string requiredAction) =>
        new(
            "Query.Search",
            "QUERY_FILTER_INVALID",
            400,
            $"Filter '{parameterName}' has unsupported value '{value}'.",
            requiredAction);
}
