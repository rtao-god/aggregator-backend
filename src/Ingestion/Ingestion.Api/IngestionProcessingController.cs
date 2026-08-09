using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Ingestion.Api;

public static class IngestionProcessingAuthorizationPolicies
{
    public const string Review = "ingestion.review";
    public const string Commit = "ingestion.commit";
}

public static class IngestionProcessingOperationIds
{
    public const string ReadProcessing = "ReadIngestionProcessing";
    public const string ReadDeliveries = "ReadIngestionCatalogDeliveries";
    public const string CompleteReview = "CompleteIngestionReview";
    public const string CommitBatch = "CommitIngestionBatch";
}

[ApiController]
[Route("api/ingestion/batches/{batchId:guid}")]
public sealed class IngestionProcessingController(
    ReadIngestionProcessingService readService,
    ReadIngestionCatalogDeliveriesService deliveryReadService,
    ReviewIngestionPackageService reviewService,
    CommitIngestionPackageService commitService) : ControllerBase
{
    [HttpGet("processing", Name = IngestionProcessingOperationIds.ReadProcessing)]
    [Authorize(Policy = IngestionAuthorizationPolicies.Read)]
    [ProducesResponseType<IngestionBatchProcessingResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IngestionBatchProcessingResponse>> ReadAsync(
        Guid batchId,
        CancellationToken cancellationToken) =>
        Ok(await readService.ReadAsync(batchId, cancellationToken));

    [HttpGet("deliveries", Name = IngestionProcessingOperationIds.ReadDeliveries)]
    [Authorize(Policy = IngestionAuthorizationPolicies.Read)]
    [ProducesResponseType<IngestionCatalogDeliveriesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IngestionCatalogDeliveriesResponse>> ReadDeliveriesAsync(
        Guid batchId,
        CancellationToken cancellationToken) =>
        Ok(await deliveryReadService.ReadAsync(batchId, cancellationToken));

    [HttpPost("review", Name = IngestionProcessingOperationIds.CompleteReview)]
    [Authorize(Policy = IngestionProcessingAuthorizationPolicies.Review)]
    [EnableRateLimiting(IngestionRateLimitPolicies.BatchCommands)]
    [ProducesResponseType<IngestionBatchProcessingResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IngestionBatchProcessingResponse>> CompleteReviewAsync(
        Guid batchId,
        [FromBody] CompleteIngestionReviewRequest request,
        CancellationToken cancellationToken) =>
        Ok(await reviewService.CompleteAsync(
            batchId,
            request,
            IngestionServiceIdentityAccessor.Require(User),
            cancellationToken));

    [HttpPost("commit", Name = IngestionProcessingOperationIds.CommitBatch)]
    [Authorize(Policy = IngestionProcessingAuthorizationPolicies.Commit)]
    [EnableRateLimiting(IngestionRateLimitPolicies.BatchCommands)]
    [ProducesResponseType<IngestionCommitResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IngestionCommitResponse>> CommitAsync(
        Guid batchId,
        [FromBody] CommitIngestionBatchRequest request,
        CancellationToken cancellationToken)
    {
        var result = await commitService.BeginAsync(
            batchId,
            request,
            RequireIdempotencyKey(Request),
            IngestionServiceIdentityAccessor.Require(User),
            cancellationToken);
        return Ok(new IngestionCommitResponse(
            result.Processing.Batch.Id.Value,
            result.Processing.Batch.State.ToString(),
            result.Processing.Batch.AggregateRevision,
            result.Deliveries,
            result.Replayed));
    }

    private static string RequireIdempotencyKey(HttpRequest request)
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
                "Retry with one stable Idempotency-Key for this commit command.");
        }

        return values[0]!;
    }
}
