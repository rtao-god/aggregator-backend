using Aggregator.Promotion.Application;
using Aggregator.Promotion.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Promotion.Api;

[ApiController]
[Route("api/promotion/products")]
public sealed class PromotionProductsController(PromotionProductService service) : ControllerBase
{
    [HttpPost(Name = PromotionOperationIds.CreateProduct)]
    [Authorize(Policy = PromotionAuthorizationPolicies.ManageCatalog)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<PromotionProductResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<PromotionProductResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionProductResponse>> CreateAsync(
        [FromBody] CreatePromotionProductRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.CreateAsync(
            request,
            PromotionHttpCommandContext.Create(HttpContext),
            PromotionHttpCommandContext.RequireIdempotencyKey(Request),
            cancellationToken);
        return result.Replayed
            ? Ok(result.Response)
            : StatusCode(StatusCodes.Status201Created, result.Response);
    }

    [HttpPost("{productId:guid}/revisions", Name = PromotionOperationIds.AddProductRevision)]
    [Authorize(Policy = PromotionAuthorizationPolicies.ManageCatalog)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<PromotionProductResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionProductResponse>> AddRevisionAsync(
        Guid productId,
        [FromBody] CreatePromotionProductRevisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.AddRevisionAsync(
            productId,
            request,
            PromotionHttpCommandContext.Create(HttpContext),
            PromotionHttpCommandContext.RequireIdempotencyKey(Request),
            cancellationToken);
        return Ok(result.Response);
    }

    [HttpPost("{productId:guid}/state", Name = PromotionOperationIds.ChangeProductState)]
    [Authorize(Policy = PromotionAuthorizationPolicies.ManageCatalog)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<PromotionProductResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionProductResponse>> ChangeStateAsync(
        Guid productId,
        [FromBody] ChangePromotionProductStateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.ChangeStateAsync(
            productId,
            request,
            PromotionHttpCommandContext.Create(HttpContext),
            PromotionHttpCommandContext.RequireIdempotencyKey(Request),
            cancellationToken);
        return Ok(result.Response);
    }

    [HttpGet("{productId:guid}", Name = PromotionOperationIds.GetProduct)]
    [Authorize(Policy = PromotionAuthorizationPolicies.Read)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Reads)]
    [ProducesResponseType<PromotionProductResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionProductResponse>> GetAsync(
        Guid productId,
        CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(productId, cancellationToken));
}
