using System.Security.Claims;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Ingestion.Api;

[ApiController]
[Route("api/ingestion/batches/{batchId:guid}")]
[Authorize]
[EnableRateLimiting(IngestionRateLimitPolicies.BatchCommands)]
public sealed class IngestionReviewCommitController(
    CompleteIngestionReviewService reviewService,
    BeginIngestionCommitService beginCommitService,
    CompleteIngestionCommitService completeCommitService) : ControllerBase
{
    [HttpPost("review-complete", Name = "CompleteIngestionReview")]
    [ProducesResponseType<IngestionWorkflowCommandResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IngestionWorkflowCommandResponse>> CompleteReviewAsync(
        Guid batchId,
        [FromBody] CompleteIngestionReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Items);
        IngestionWorkflowAccess.RequireScope(User, "ingestion.review");
        var result = await reviewService.CompleteAsync(
            new CompleteIngestionReviewCommand(
                batchId,
                request.ExpectedAggregateRevision,
                request.Items.Select(item =>
                    new IngestionReviewResolution(
                        item.ItemKey,
                        item.Decision,
                        item.ReasonCodes)).ToArray(),
                IngestionWorkflowAccess.RequireIdempotencyKey(Request),
                IngestionWorkflowAccess.RequireSubject(User)),
            cancellationToken);
        return Ok(ToResponse(result));
    }

    [HttpPost("commit", Name = "BeginIngestionCommit")]
    [ProducesResponseType<IngestionWorkflowCommandResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IngestionWorkflowCommandResponse>> BeginCommitAsync(
        Guid batchId,
        [FromBody] BeginIngestionCommitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SelectedItemKeys);
        IngestionWorkflowAccess.RequireScope(User, "ingestion.commit");
        var result = await beginCommitService.BeginAsync(
            new BeginIngestionCommitCommand(
                batchId,
                request.ExpectedAggregateRevision,
                request.SelectedItemKeys,
                IngestionWorkflowAccess.RequireIdempotencyKey(Request),
                IngestionWorkflowAccess.RequireSubject(User)),
            cancellationToken);
        return Ok(ToResponse(result));
    }

    [HttpPost("commit-complete", Name = "CompleteIngestionCommit")]
    [ProducesResponseType<IngestionWorkflowCommandResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IngestionWorkflowCommandResponse>> CompleteCommitAsync(
        Guid batchId,
        [FromBody] CompleteIngestionCommitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Outcomes);
        IngestionWorkflowAccess.RequireScope(User, "ingestion.commit");
        var result = await completeCommitService.CompleteAsync(
            new CompleteIngestionCommitCommand(
                batchId,
                request.ExpectedAggregateRevision,
                request.Outcomes.Select(item =>
                    new IngestionCatalogDeliveryOutcome(
                        item.ItemKey,
                        item.CommandId,
                        item.Outcome,
                        item.CatalogSubjectId,
                        item.CatalogListingId,
                        item.CatalogListingRevisionId,
                        item.FailureCode)).ToArray(),
                IngestionWorkflowAccess.RequireIdempotencyKey(Request),
                IngestionWorkflowAccess.RequireSubject(User)),
            cancellationToken);
        return Ok(ToResponse(result));
    }

    private static IngestionWorkflowCommandResponse ToResponse(IngestionBatchCommandResult result) =>
        new(IngestionBatchContractMapper.ToDto(result.Batch), result.Replayed);
}

internal static class IngestionWorkflowAccess
{
    public static string RequireSubject(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var subject = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200)
        {
            throw new IngestionApplicationException(
                "Ingestion.Access",
                "INGESTION_OPERATOR_IDENTITY_REQUIRED",
                StatusCodes.Status403Forbidden,
                "The authenticated token has no valid operator or workload subject.",
                "Authenticate with an identity containing the exact OIDC subject.");
        }

        return subject;
    }

    public static void RequireScope(ClaimsPrincipal principal, string requiredScope)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredScope);
        var scopes = principal.FindAll("scope")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        if (!scopes.Contains(requiredScope))
        {
            throw new IngestionApplicationException(
                "Ingestion.Access",
                "INGESTION_WORKFLOW_SCOPE_REQUIRED",
                StatusCodes.Status403Forbidden,
                $"The authenticated identity lacks required scope '{requiredScope}'.",
                "Request the exact review or commit scope for this operation.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["requiredScope"] = requiredScope,
                });
        }
    }

    public static string RequireIdempotencyKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values) ||
            values.Count != 1 ||
            string.IsNullOrWhiteSpace(values[0]))
        {
            throw new IngestionApplicationException(
                "Ingestion.Commands",
                "INGESTION_IDEMPOTENCY_KEY_REQUIRED",
                StatusCodes.Status400BadRequest,
                "Exactly one non-empty Idempotency-Key header is required.",
                "Retry with one stable Idempotency-Key for this workflow command.");
        }

        return values[0]!;
    }
}
