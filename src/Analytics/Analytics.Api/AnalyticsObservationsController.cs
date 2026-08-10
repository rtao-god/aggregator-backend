using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Analytics.Api;

[ApiController]
[Route("api/analytics")]
public sealed class AnalyticsInteractionsController(
    SubmitInteractionEventService interactionService,
    SubmitInteractionEventBatchService interactionBatchService,
    AnalyticsAntiAbuseProofService antiAbuseProofService) : ControllerBase
{
    [HttpPost("anti-abuse-tokens", Name = AnalyticsOperationIds.IssueAntiAbuseToken)]
    [AllowAnonymous]
    [EnableRateLimiting(AnalyticsRateLimitPolicies.AntiAbuseTokens)]
    [ProducesResponseType<AnalyticsAntiAbuseTokenResponse>(StatusCodes.Status200OK)]
    public ActionResult<AnalyticsAntiAbuseTokenResponse> IssueAntiAbuseToken(
        [FromBody] IssueAnalyticsAntiAbuseTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(antiAbuseProofService.Issue(request));
    }

    [HttpPost("interaction-events", Name = AnalyticsOperationIds.SubmitInteractionEvent)]
    [AllowAnonymous]
    [EnableRateLimiting(AnalyticsRateLimitPolicies.InteractionEvents)]
    [ProducesResponseType<InteractionEventResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<InteractionEventResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<InteractionEventResponse>> SubmitAsync(
        [FromBody] SubmitInteractionEventRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await interactionService.SubmitAsync(request, cancellationToken);
        return response.AcceptanceState == InteractionAcceptanceStateContract.AlreadyApplied
            ? Ok(response)
            : StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("interaction-events/batch", Name = AnalyticsOperationIds.SubmitInteractionEventBatch)]
    [AllowAnonymous]
    [EnableRateLimiting(AnalyticsRateLimitPolicies.InteractionEvents)]
    [RequestSizeLimit(AnalyticsRequestLimits.InteractionEventBatchMaximumBodyBytes)]
    [ProducesResponseType<InteractionEventBatchResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<InteractionEventBatchResponse>> SubmitBatchAsync(
        [FromBody] SubmitInteractionEventBatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await interactionBatchService.SubmitAsync(request, cancellationToken));
    }
}

[ApiController]
[Route("api/analytics/listings/{listingId:guid}")]
[Authorize(Policy = AnalyticsAuthorizationPolicies.ViewListing)]
[EnableRateLimiting(AnalyticsRateLimitPolicies.Metrics)]
public sealed class AnalyticsMetricsController(
    ReadDailyListingMetricsService metricsService) : ControllerBase
{
    [HttpGet("daily-metrics", Name = AnalyticsOperationIds.ReadDailyListingMetrics)]
    [ProducesResponseType<IReadOnlyList<DailyListingMetricsResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DailyListingMetricsResponse>>> ReadDailyAsync(
        Guid listingId,
        [FromQuery] string catalogKey,
        [FromQuery] DateOnly fromInclusive,
        [FromQuery] DateOnly toExclusive,
        CancellationToken cancellationToken) =>
        Ok(await metricsService.ReadAsync(
            AnalyticsActorAccessor.Require(HttpContext),
            catalogKey,
            listingId,
            new DailyMetricsRangeRequest(fromInclusive, toExclusive),
            cancellationToken));
}

[ApiController]
[Route("api/analytics")]
[Authorize(Policy = AnalyticsAuthorizationPolicies.ViewAggregationStatus)]
[EnableRateLimiting(AnalyticsRateLimitPolicies.Metrics)]
public sealed class AnalyticsAggregationController(
    ReadAnalyticsAggregationStatusService statusService) : ControllerBase
{
    [HttpGet("aggregation-status", Name = AnalyticsOperationIds.ReadAggregationStatus)]
    [ProducesResponseType<AnalyticsAggregationStatusResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalyticsAggregationStatusResponse>> ReadStatusAsync(
        [FromQuery] DateOnly fromInclusive,
        [FromQuery] DateOnly toExclusive,
        CancellationToken cancellationToken) =>
        Ok(await statusService.ReadAsync(
            new DailyMetricsRangeRequest(fromInclusive, toExclusive),
            cancellationToken));
}
