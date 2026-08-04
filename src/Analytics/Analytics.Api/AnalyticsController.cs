using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Analytics.Api;

[ApiController]
[Route("api/analytics")]
public sealed class AnalyticsController(
    AnalyticsRuntimeService service,
    AnalyticsApiOptions apiOptions) : ControllerBase
{
    [HttpPost("interactions", Name = "RecordAnalyticsInteraction")]
    [EnableRateLimiting("analytics-intake")]
    [ProducesResponseType<AnalyticsInteractionReceipt>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AnalyticsInteractionReceipt>> RecordAsync(
        [FromBody] RecordAnalyticsInteractionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var receipt = await service.RecordAsync(request, cancellationToken);
        return Ok(receipt);
    }

    [HttpGet("catalogs/{catalogKey}/listings/{listingId:guid}/metrics", Name = "GetAnalyticsListingMetrics")]
    [ProducesResponseType<AnalyticsListingMetricsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AnalyticsListingMetricsResponse>> GetMetricsAsync(
        string catalogKey,
        Guid listingId,
        CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("X-Analytics-Internal-Key", out var suppliedKey) ||
            !apiOptions.IsInternalKeyValid(suppliedKey.ToString()))
        {
            return Unauthorized(new
            {
                owner = "Analytics.InternalRead",
                code = "ANALYTICS_INTERNAL_KEY_INVALID",
                requiredAction = "Supply the configured internal metrics key from an authorized backend workload.",
            });
        }

        var metrics = await service.ReadListingMetricsAsync(
            catalogKey,
            listingId,
            cancellationToken);
        return Ok(metrics);
    }
}
