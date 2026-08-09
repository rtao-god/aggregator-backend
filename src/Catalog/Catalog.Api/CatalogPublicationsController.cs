using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Platform.ProblemDetails;

namespace Aggregator.Catalog.Api;

[ApiController]
[Route("api/catalog-command/catalogs/{catalogKey}")]
[EnableRateLimiting(CatalogRateLimitPolicies.Command)]
public sealed class CatalogPublicationsController(
    CatalogPublicationOperationService operationService,
    CatalogPublicationService publicationService,
    ICorrelationContextAccessor correlation) : ControllerBase
{
    [HttpPost("publication-requests", Name = CatalogOperationIds.CreatePublication)]
    [Authorize(Policy = CatalogAuthorizationPolicies.Publish)]
    [ProducesResponseType<CatalogPublicationOperationResponse>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<CatalogPublicationOperationResponse>> PublishAsync(
        string catalogKey,
        [FromBody] CreateCatalogPublicationRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(catalogKey, request.CatalogKey, StringComparison.Ordinal))
        {
            throw new CatalogContractException(
                "catalog.publication_route_catalog_mismatch",
                "The route catalog key must match the publication request catalog key.");
        }

        var response = await operationService.EnqueueAsync(
            request,
            CatalogActorAccessor.Require(HttpContext),
            CatalogEventContextAccessor.Require(correlation),
            idempotencyKey,
            cancellationToken);
        return Accepted(
            $"/api/catalog-command/operations/{response.OperationId:D}",
            response);
    }

    [HttpPost("publication-rollbacks", Name = CatalogOperationIds.RollbackPublication)]
    [Authorize(Policy = CatalogAuthorizationPolicies.Rollback)]
    [ProducesResponseType<CatalogPublicationResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CatalogPublicationResponse>> RollbackAsync(
        string catalogKey,
        [FromBody] RollbackPublicationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await publicationService.RollbackAsync(
            catalogKey,
            request,
            CatalogActorAccessor.Require(HttpContext),
            CatalogEventContextAccessor.Require(correlation),
            cancellationToken));
    }
}
