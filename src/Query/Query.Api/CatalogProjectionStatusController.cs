using Aggregator.Query.Application;
using Aggregator.Query.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Query.Api;

[ApiController]
[EnableRateLimiting("public-query")]
[Route("api/catalog-query/catalogs/{catalogKey}/projection-status")]
public sealed class CatalogProjectionStatusController(
    ReadPublicProjectionStatusService service) : ControllerBase
{
    [HttpGet(Name = "GetPublicProjectionStatus")]
    [ProducesResponseType<PublicCatalogProjectionStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<PublicCatalogProjectionStatusResponse>> Get(
        [FromRoute] string catalogKey,
        CancellationToken cancellationToken)
    {
        var response = await service.ReadAsync(catalogKey, cancellationToken);
        Response.Headers.CacheControl = "public,max-age=15,must-revalidate";
        return Ok(response);
    }
}
