using Aggregator.Promotion.Application;
using Aggregator.Promotion.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Promotion.Api;

[ApiController]
[Route("api/promotion/placements")]
public sealed class PromotionPlacementsController(PromotionPlacementService service) : ControllerBase
{
    [HttpPost(Name = PromotionOperationIds.CreatePlacement)]
    [Authorize(Policy = PromotionAuthorizationPolicies.ManageCatalog)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<SponsoredPlacementResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<SponsoredPlacementResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SponsoredPlacementResponse>> CreateAsync(
        [FromBody] CreateSponsoredPlacementRequest request,
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

    [HttpPost("{placementId:guid}/revisions", Name = PromotionOperationIds.AddPlacementRevision)]
    [Authorize(Policy = PromotionAuthorizationPolicies.ManageCatalog)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<SponsoredPlacementResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SponsoredPlacementResponse>> AddRevisionAsync(
        Guid placementId,
        [FromBody] CreateSponsoredPlacementRevisionRequest request,
        CancellationToken cancellationToken) =>
        Ok((await service.ReviseAsync(
            placementId,
            request,
            PromotionHttpCommandContext.Create(HttpContext),
            PromotionHttpCommandContext.RequireIdempotencyKey(Request),
            cancellationToken)).Response);

    [HttpPost("{placementId:guid}/pause", Name = PromotionOperationIds.PausePlacement)]
    [Authorize(Policy = PromotionAuthorizationPolicies.ManageCatalog)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<SponsoredPlacementResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SponsoredPlacementResponse>> PauseAsync(
        Guid placementId,
        [FromBody] ChangeSponsoredPlacementStateRequest request,
        CancellationToken cancellationToken) =>
        Ok((await service.PauseAsync(
            placementId,
            request,
            PromotionHttpCommandContext.Create(HttpContext),
            PromotionHttpCommandContext.RequireIdempotencyKey(Request),
            cancellationToken)).Response);

    [HttpPost("{placementId:guid}/resume", Name = PromotionOperationIds.ResumePlacement)]
    [Authorize(Policy = PromotionAuthorizationPolicies.ManageCatalog)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<SponsoredPlacementResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SponsoredPlacementResponse>> ResumeAsync(
        Guid placementId,
        [FromBody] ChangeSponsoredPlacementStateRequest request,
        CancellationToken cancellationToken) =>
        Ok((await service.ResumeAsync(
            placementId,
            request,
            PromotionHttpCommandContext.Create(HttpContext),
            PromotionHttpCommandContext.RequireIdempotencyKey(Request),
            cancellationToken)).Response);

    [HttpPost("{placementId:guid}/revoke", Name = PromotionOperationIds.RevokePlacement)]
    [Authorize(Policy = PromotionAuthorizationPolicies.ManageCatalog)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<SponsoredPlacementResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SponsoredPlacementResponse>> RevokeAsync(
        Guid placementId,
        [FromBody] ChangeSponsoredPlacementStateRequest request,
        CancellationToken cancellationToken) =>
        Ok((await service.RevokeAsync(
            placementId,
            request,
            PromotionHttpCommandContext.Create(HttpContext),
            PromotionHttpCommandContext.RequireIdempotencyKey(Request),
            cancellationToken)).Response);

    [HttpGet("{placementId:guid}", Name = PromotionOperationIds.GetPlacement)]
    [Authorize(Policy = PromotionAuthorizationPolicies.Read)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Reads)]
    [ProducesResponseType<SponsoredPlacementResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SponsoredPlacementResponse>> GetAsync(
        Guid placementId,
        CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(placementId, cancellationToken));

    [HttpGet(
        "~/api/promotion/catalogs/{catalogKey}/placement-calendar",
        Name = PromotionOperationIds.GetPlacementCalendar)]
    [Authorize(Policy = PromotionAuthorizationPolicies.Read)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Reads)]
    [ProducesResponseType<PromotionPlacementCalendarResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionPlacementCalendarResponse>> ReadCalendarAsync(
        string catalogKey,
        [FromQuery] DateTimeOffset fromUtc,
        [FromQuery] DateTimeOffset toUtc,
        CancellationToken cancellationToken) =>
        Ok(await service.ReadCalendarAsync(catalogKey, fromUtc, toUtc, cancellationToken));
}
