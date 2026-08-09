using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Platform.Observability;

namespace Aggregator.Catalog.Api;

[ApiController]
[Route("api/catalog-command/listings/{listingId:guid}/disputes")]
[EnableRateLimiting(CatalogRateLimitPolicies.Command)]
public sealed class CatalogListingDisputesController(
    CatalogListingDisputeService service,
    ICorrelationContextAccessor correlation) : ControllerBase
{
    [HttpPost(Name = CatalogOperationIds.OpenListingDispute)]
    [Authorize(Policy = CatalogAuthorizationPolicies.Review)]
    [ProducesResponseType<CatalogListingDisputeResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CatalogListingDisputeResponse>> OpenAsync(
        Guid listingId,
        [FromBody] OpenCatalogListingDisputeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await service.OpenAsync(
            listingId,
            request,
            CatalogActorAccessor.Require(HttpContext),
            CatalogEventContextAccessor.Require(correlation),
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("{disputeId:guid}/resolution", Name = CatalogOperationIds.ResolveListingDispute)]
    [Authorize(Policy = CatalogAuthorizationPolicies.Review)]
    [ProducesResponseType<CatalogListingDisputeResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CatalogListingDisputeResponse>> ResolveAsync(
        Guid listingId,
        Guid disputeId,
        [FromBody] ResolveCatalogListingDisputeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await service.ResolveAsync(
            listingId,
            disputeId,
            request,
            CatalogActorAccessor.Require(HttpContext),
            CatalogEventContextAccessor.Require(correlation),
            cancellationToken));
    }
}
