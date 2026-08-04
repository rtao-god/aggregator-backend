using Aggregator.Ingestion.Collector.Application;
using Aggregator.Ingestion.Collector.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Ingestion.Collector.Api;

[ApiController]
[Route("api/collector-candidates")]
[Authorize(Policy = "ingestion.submit")]
[EnableRateLimiting("collector-intake")]
public sealed class CollectorCandidatesController(
    CollectorCandidateService service) : ControllerBase
{
    [HttpPost(Name = "SubmitCollectorCandidate")]
    [ProducesResponseType<CollectorCandidateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CollectorCandidateResponse>> SubmitAsync(
        [FromBody] SubmitCollectorCandidateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await service.SubmitAsync(request, cancellationToken);
        return Ok(response);
    }
}
