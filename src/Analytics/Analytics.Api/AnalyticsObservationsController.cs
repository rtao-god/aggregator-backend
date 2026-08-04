using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Analytics.Api;

public static class AnalyticsOperationIds
{
    public const string RecordObservation = "RecordAnalyticsObservation";

    public const string ReadMetrics = "ReadAnalyticsMetrics";
}

[ApiController]
[Route("api/analytics")]
public sealed class AnalyticsObservationsController(
    RecordAnalyticsObservationService observationService,
    ReadAnalyticsMetricsService metricsService) : ControllerBase
{
    [HttpPost("observations", Name = AnalyticsOperationIds.RecordObservation)]
    [Authorize(Policy = AnalyticsAuthorizationPolicies.Write)]
    [EnableRateLimiting(AnalyticsRateLimitPolicies.Write)]
    [ProducesResponseType<AnalyticsObservationReceipt>(StatusCodes.Status201Created)]
    [ProducesResponseType<AnalyticsObservationReceipt>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalyticsObservationReceipt>> RecordAsync(
        [FromBody] RecordAnalyticsObservationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var receipt = await observationService.RecordAsync(request, cancellationToken);
        return receipt.Replayed
            ? Ok(receipt)
            : StatusCode(StatusCodes.Status201Created, receipt);
    }

    [HttpGet(
        "catalogs/{catalogKey}/public-read-revisions/{publicReadRevisionId:guid}/metrics",
        Name = AnalyticsOperationIds.ReadMetrics)]
    [Authorize(Policy = AnalyticsAuthorizationPolicies.Read)]
    [EnableRateLimiting(AnalyticsRateLimitPolicies.Read)]
    [ProducesResponseType<AnalyticsMetricsResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalyticsMetricsResponse>> ReadMetricsAsync(
        string catalogKey,
        Guid publicReadRevisionId,
        [FromQuery] DateOnly fromDate,
        [FromQuery] DateOnly toDate,
        CancellationToken cancellationToken) =>
        Ok(await metricsService.ReadAsync(
            catalogKey,
            publicReadRevisionId,
            fromDate,
            toDate,
            cancellationToken));
}
