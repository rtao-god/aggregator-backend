using System.Security.Claims;
using Aggregator.Promotion.Application;
using Aggregator.Promotion.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Promotion.Api;

public static class PromotionOperationIds
{
    public const string CreateCampaign = "CreatePromotionCampaign";
    public const string GetCampaign = "GetPromotionCampaign";
    public const string ActivateCampaign = "ActivatePromotionCampaign";
    public const string SuspendCampaign = "SuspendPromotionCampaign";
    public const string ResumeCampaign = "ResumePromotionCampaign";
    public const string CancelCampaign = "CancelPromotionCampaign";
    public const string ReadSponsoredPlacement = "ReadSponsoredPlacement";
}

[ApiController]
[Route("api/promotion")]
public sealed class PromotionCampaignsController(PromotionCampaignService service) : ControllerBase
{
    [HttpPost("campaigns", Name = PromotionOperationIds.CreateCampaign)]
    [Authorize(Policy = PromotionAuthorizationPolicies.Manage)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<PromotionCampaignResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<PromotionCampaignResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionCampaignResponse>> CreateAsync(
        [FromBody] CreatePromotionCampaignRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await service.CreateAsync(
            request,
            RequireIdempotencyKey(Request),
            RequireCallerIdentity(User),
            cancellationToken);
        return response.Replayed
            ? Ok(response)
            : CreatedAtRoute(
                PromotionOperationIds.GetCampaign,
                new { campaignId = response.Id },
                response);
    }

    [HttpGet("campaigns/{campaignId:guid}", Name = PromotionOperationIds.GetCampaign)]
    [Authorize(Policy = PromotionAuthorizationPolicies.Read)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Reads)]
    [ProducesResponseType<PromotionCampaignResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionCampaignResponse>> GetAsync(
        Guid campaignId,
        CancellationToken cancellationToken) =>
        Ok(await service.ReadAsync(campaignId, cancellationToken));

    [HttpPost("campaigns/{campaignId:guid}/activate", Name = PromotionOperationIds.ActivateCampaign)]
    [Authorize(Policy = PromotionAuthorizationPolicies.Manage)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<PromotionCampaignResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionCampaignResponse>> ActivateAsync(
        Guid campaignId,
        [FromBody] PromotionCampaignRevisionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await service.ActivateAsync(
            campaignId,
            request,
            RequireIdempotencyKey(Request),
            RequireCallerIdentity(User),
            cancellationToken));

    [HttpPost("campaigns/{campaignId:guid}/suspend", Name = PromotionOperationIds.SuspendCampaign)]
    [Authorize(Policy = PromotionAuthorizationPolicies.Manage)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<PromotionCampaignResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionCampaignResponse>> SuspendAsync(
        Guid campaignId,
        [FromBody] SuspendPromotionCampaignRequest request,
        CancellationToken cancellationToken) =>
        Ok(await service.SuspendAsync(
            campaignId,
            request,
            RequireIdempotencyKey(Request),
            RequireCallerIdentity(User),
            cancellationToken));

    [HttpPost("campaigns/{campaignId:guid}/resume", Name = PromotionOperationIds.ResumeCampaign)]
    [Authorize(Policy = PromotionAuthorizationPolicies.Manage)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<PromotionCampaignResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionCampaignResponse>> ResumeAsync(
        Guid campaignId,
        [FromBody] PromotionCampaignRevisionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await service.ResumeAsync(
            campaignId,
            request,
            RequireIdempotencyKey(Request),
            RequireCallerIdentity(User),
            cancellationToken));

    [HttpPost("campaigns/{campaignId:guid}/cancel", Name = PromotionOperationIds.CancelCampaign)]
    [Authorize(Policy = PromotionAuthorizationPolicies.Manage)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Commands)]
    [ProducesResponseType<PromotionCampaignResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PromotionCampaignResponse>> CancelAsync(
        Guid campaignId,
        [FromBody] PromotionCampaignRevisionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await service.CancelAsync(
            campaignId,
            request,
            RequireIdempotencyKey(Request),
            RequireCallerIdentity(User),
            cancellationToken));

    [HttpGet(
        "catalogs/{catalogKey}/placements/{placementKey}/sponsored",
        Name = PromotionOperationIds.ReadSponsoredPlacement)]
    [Authorize(Policy = PromotionAuthorizationPolicies.Read)]
    [EnableRateLimiting(PromotionRateLimitPolicies.Reads)]
    [ProducesResponseType<SponsoredPlacementResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SponsoredPlacementResponse>> ReadSponsoredPlacementAsync(
        string catalogKey,
        string placementKey,
        [FromQuery] DateTimeOffset? effectiveAtUtc,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await service.ReadSponsoredPlacementAsync(
            catalogKey,
            placementKey,
            effectiveAtUtc,
            limit,
            cancellationToken));

    private static string RequireIdempotencyKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values) ||
            values.Count != 1 ||
            string.IsNullOrWhiteSpace(values[0]))
        {
            throw new PromotionCampaignApplicationException(
                "Promotion.Commands",
                "PROMOTION_IDEMPOTENCY_KEY_REQUIRED",
                StatusCodes.Status400BadRequest,
                "Exactly one non-empty Idempotency-Key header is required.",
                "Retry with one stable Idempotency-Key for this command.");
        }

        return values[0]!;
    }

    private static string RequireCallerIdentity(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var subject = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200)
        {
            throw new PromotionCampaignApplicationException(
                "Promotion.Access",
                "PROMOTION_CALLER_IDENTITY_REQUIRED",
                StatusCodes.Status403Forbidden,
                "The authenticated token has no valid workload subject claim.",
                "Authenticate with an OIDC workload identity containing the exact subject.");
        }

        return subject;
    }
}
