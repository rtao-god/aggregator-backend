using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Catalog.Api;

[ApiController]
[Route("api/catalog-command/catalogs/{catalogKey}")]
[EnableRateLimiting(CatalogRateLimitPolicies.Command)]
public sealed class CatalogPublicationsController(CatalogPublicationService service) : ControllerBase
{
    [HttpPost("publication-requests", Name = CatalogOperationIds.CreatePublication)]
    [Authorize(Policy = CatalogAuthorizationPolicies.Publish)]
    [ProducesResponseType<CatalogPublicationResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CatalogPublicationResponse>> PublishAsync(
        string catalogKey,
        [FromBody] CreateCatalogPublicationRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(catalogKey, request.CatalogKey, StringComparison.Ordinal))
        {
            throw new CatalogContractException(
                "catalog.publication_route_catalog_mismatch",
                "The route catalog key must match the publication request catalog key.");
        }

        var response = await service.PublishAsync(
            request,
            CatalogActorAccessor.Require(HttpContext),
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("publication-rollbacks", Name = CatalogOperationIds.RollbackPublication)]
    [Authorize(Policy = CatalogAuthorizationPolicies.Rollback)]
    [ProducesResponseType<CatalogPublicationResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CatalogPublicationResponse>> RollbackAsync(
        string catalogKey,
        [FromBody] RollbackPublicationRequest request,
        CancellationToken cancellationToken) =>
        Ok(await service.RollbackAsync(
            catalogKey,
            request,
            CatalogActorAccessor.Require(HttpContext),
            cancellationToken));
}
