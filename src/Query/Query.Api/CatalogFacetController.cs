using Aggregator.Query.Application;
using Aggregator.Query.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Aggregator.Query.Api;

[ApiController]
[Route("api/catalog-query/catalogs/{catalogKey}/facets")]
public sealed class CatalogFacetController(
    PublicFacetCatalogService service) : ControllerBase
{
    [HttpGet(Name = "GetPublicFacetCatalog")]
    [ProducesResponseType<PublicFacetCatalogResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PublicFacetCatalogResponse>> Get(
        [FromRoute] string catalogKey,
        CancellationToken cancellationToken = default)
    {
        if (Request.Query.Count > 0)
        {
            var unknown = Request.Query.Keys
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            throw new QueryReadException(
                "Query.Facets",
                "QUERY_FACET_PARAMETER_UNKNOWN",
                400,
                $"Unknown public facet parameter(s): {string.Join(", ", unknown)}.",
                "Remove query parameters; the facet catalog describes the complete active projection.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["unknownParameters"] = unknown,
                });
        }

        var response = await service.GetAsync(catalogKey, cancellationToken);
        Response.Headers.ETag = QueryHttpCache.BuildETag(
            response.Metadata.PublicReadRevisionId);
        Response.Headers.CacheControl = "public,max-age=300,stale-while-revalidate=1800";
        return Ok(response);
    }
}
