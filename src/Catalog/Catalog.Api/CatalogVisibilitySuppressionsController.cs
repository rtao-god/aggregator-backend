using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Platform.ProblemDetails;

namespace Aggregator.Catalog.Api;

[ApiController]
[Route("api/catalog-command/catalogs/{catalogKey}/visibility-suppressions")]
[EnableRateLimiting(CatalogRateLimitPolicies.Command)]
public sealed class CatalogVisibilitySuppressionsController(
    CatalogVisibilitySuppressionService service,
    ICorrelationContextAccessor correlation) : ControllerBase
{
    [HttpPost(Name = CatalogOperationIds.CreateVisibilitySuppression)]
    [Authorize(Policy = CatalogAuthorizationPolicies.ManageVisibility)]
    [ProducesResponseType<PublicVisibilitySuppressionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PublicVisibilitySuppressionResponse>> CreateAsync(
        string catalogKey,
        [FromBody] CreatePublicVisibilitySuppressionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await service.CreateActiveAsync(
            catalogKey,
            request,
            CatalogActorAccessor.Require(HttpContext),
            CatalogEventContextAccessor.Require(correlation),
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("{suppressionId:guid}/resolve", Name = CatalogOperationIds.ResolveVisibilitySuppression)]
    [Authorize(Policy = CatalogAuthorizationPolicies.ManageVisibility)]
    [ProducesResponseType<PublicVisibilitySuppressionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PublicVisibilitySuppressionResponse>> ResolveAsync(
        string catalogKey,
        Guid suppressionId,
        [FromBody] ResolvePublicVisibilitySuppressionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await service.ResolveAsync(
            catalogKey,
            suppressionId,
            request,
            CatalogActorAccessor.Require(HttpContext),
            CatalogEventContextAccessor.Require(correlation),
            cancellationToken));
    }
}
