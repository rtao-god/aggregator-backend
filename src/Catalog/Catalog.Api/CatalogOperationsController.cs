using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Catalog.Api;

[ApiController]
[Route("api/catalog-command/operations")]
[EnableRateLimiting(CatalogRateLimitPolicies.Command)]
public sealed class CatalogOperationsController(
    CatalogPublicationOperationService operationService) : ControllerBase
{
    [HttpGet("{operationId:guid}", Name = CatalogOperationIds.GetOperation)]
    [Authorize(Policy = CatalogAuthorizationPolicies.Publish)]
    [ProducesResponseType<CatalogPublicationOperationResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CatalogPublicationOperationResponse>> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        Ok(await operationService.GetAsync(
            operationId,
            CatalogActorAccessor.Require(HttpContext),
            cancellationToken));
}
