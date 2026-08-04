using Aggregator.Promotion.Application;
using Aggregator.Promotion.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Promotion.Api;

[ApiController]
[Route("api/promotion/entitlements")]
public sealed class PromotionEntitlementsController(PromotionEntitlementService service) : ControllerBase
{
    [HttpPost(Name = PromotionOperationIds.GrantEntitlement)]
    [Authorize(Policy = PromotionAuthorizationPolicies.ManageListing)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<PromotionEntitlementResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<PromotionEntitlementResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionEntitlementResponse>> GrantAsync(
        [FromBody] GrantPromotionEntitlementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.GrantAsync(
            request,
            PromotionHttpCommandContext.Create(HttpContext),
            PromotionHttpCommandContext.RequireIdempotencyKey(Request),
            cancellationToken);
        return result.Replayed
            ? Ok(result.Response)
            : StatusCode(StatusCodes.Status201Created, result.Response);
    }

    [HttpPost("{entitlementId:guid}/pause", Name = PromotionOperationIds.PauseEntitlement)]
    [Authorize(Policy = PromotionAuthorizationPolicies.ManageListing)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<PromotionEntitlementResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionEntitlementResponse>> PauseAsync(
        Guid entitlementId,
        [FromBody] ChangePromotionEntitlementStateRequest request,
        CancellationToken cancellationToken) =>
        Ok((await service.PauseAsync(
            entitlementId,
            request,
            PromotionHttpCommandContext.Create(HttpContext),
            PromotionHttpCommandContext.RequireIdempotencyKey(Request),
            cancellationToken)).Response);

    [HttpPost("{entitlementId:guid}/resume", Name = PromotionOperationIds.ResumeEntitlement)]
    [Authorize(Policy = PromotionAuthorizationPolicies.ManageListing)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<PromotionEntitlementResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionEntitlementResponse>> ResumeAsync(
        Guid entitlementId,
        [FromBody] ChangePromotionEntitlementStateRequest request,
        CancellationToken cancellationToken) =>
        Ok((await service.ResumeAsync(
            entitlementId,
            request,
            PromotionHttpCommandContext.Create(HttpContext),
            PromotionHttpCommandContext.RequireIdempotencyKey(Request),
            cancellationToken)).Response);

    [HttpPost("{entitlementId:guid}/revoke", Name = PromotionOperationIds.RevokeEntitlement)]
    [Authorize(Policy = PromotionAuthorizationPolicies.ManageListing)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<PromotionEntitlementResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionEntitlementResponse>> RevokeAsync(
        Guid entitlementId,
        [FromBody] ChangePromotionEntitlementStateRequest request,
        CancellationToken cancellationToken) =>
        Ok((await service.RevokeAsync(
            entitlementId,
            request,
            PromotionHttpCommandContext.Create(HttpContext),
            PromotionHttpCommandContext.RequireIdempotencyKey(Request),
            cancellationToken)).Response);

    [HttpGet("{entitlementId:guid}", Name = PromotionOperationIds.GetEntitlement)]
    [Authorize(Policy = PromotionAuthorizationPolicies.Read)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Reads)]
    [ProducesResponseType<PromotionEntitlementResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionEntitlementResponse>> GetAsync(
        Guid entitlementId,
        CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(entitlementId, cancellationToken));

    [HttpGet(
        "~/api/promotion/listings/{listingId:guid}/entitlements",
        Name = PromotionOperationIds.ListListingEntitlements)]
    [Authorize(Policy = PromotionAuthorizationPolicies.Read)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Reads)]
    [ProducesResponseType<IReadOnlyList<PromotionEntitlementResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PromotionEntitlementResponse>>> ListForListingAsync(
        Guid listingId,
        CancellationToken cancellationToken) =>
        Ok(await service.ListForListingAsync(listingId, cancellationToken));
}
