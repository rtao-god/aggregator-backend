using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Platform.ProblemDetails;

namespace Aggregator.Catalog.Api;

[ApiController]
[Route("api/catalog-command/catalogs")]
[EnableRateLimiting(CatalogRateLimitPolicies.Command)]
public sealed class CatalogConfigurationController(
    CatalogConfigurationService service,
    ICorrelationContextAccessor correlation) : ControllerBase
{
    [HttpPost("config-revisions", Name = CatalogOperationIds.ImportConfiguration)]
    [Authorize(Policy = CatalogAuthorizationPolicies.ManageConfiguration)]
    [ProducesResponseType<ProductConfigurationRevisionResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ProductConfigurationRevisionResponse>> ImportAsync(
        [FromBody] ImportProductConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await service.ImportAsync(
            request,
            CatalogActorAccessor.Require(HttpContext),
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("{catalogKey}/config-revisions/{revisionId:guid}/activate", Name = CatalogOperationIds.ActivateConfiguration)]
    [Authorize(Policy = CatalogAuthorizationPolicies.ManageConfiguration)]
    [ProducesResponseType<ProductConfigurationRevisionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProductConfigurationRevisionResponse>> ActivateAsync(
        string catalogKey,
        Guid revisionId,
        [FromBody] ActivateProductConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TargetConfigurationRevisionId != revisionId)
        {
            throw new CatalogContractException(
                "catalog.configuration_route_revision_mismatch",
                "The route revision ID must match the activation request target revision ID.");
        }

        var response = await service.ActivateAsync(
            catalogKey,
            request,
            CatalogActorAccessor.Require(HttpContext),
            CatalogEventContextAccessor.Require(correlation),
            cancellationToken);
        return Ok(response);
    }
}
