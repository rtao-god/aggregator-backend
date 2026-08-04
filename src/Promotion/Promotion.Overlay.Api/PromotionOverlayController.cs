using System.Diagnostics;
using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Overlay.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Promotion.Overlay.Api;

[ApiController]
[Route("api/promotion-overlays")]
[Authorize(Policy = PromotionOverlayAuthorizationPolicies.Publish)]
[EnableRateLimiting(PromotionOverlayRateLimitPolicies.Command)]
public sealed class PromotionOverlayController(
    PromotionOverlayPublicationService service) : ControllerBase
{
    [HttpPost(Name = "PublishPromotionOverlay")]
    [ProducesResponseType<PromotionOverlayPublicationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PromotionOverlayPublicationResponse>> PublishAsync(
        [FromBody] PublishPromotionOverlayRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var correlationId = Request.Headers.TryGetValue("X-Correlation-Id", out var header)
            && !string.IsNullOrWhiteSpace(header.ToString())
                ? header.ToString().Trim()
                : Activity.Current?.TraceId.ToString()
                    ?? Guid.CreateVersion7().ToString("D");
        var response = await service.PublishAsync(request, correlationId, cancellationToken);
        Response.Headers["X-Correlation-Id"] = correlationId;
        return Ok(response);
    }
}
