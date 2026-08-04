using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Ingestion.Api;

public static class IngestionOperationIds
{
    public const string RegisterBatch = "RegisterIngestionBatch";

    public const string GetBatch = "GetIngestionBatch";

    public const string PrepareBatchUpload = "PrepareIngestionBatchUpload";

    public const string CompleteBatchUpload = "CompleteIngestionBatchUpload";
}

[ApiController]
[Route("api/ingestion/batches")]
[EnableRateLimiting(IngestionRateLimitPolicies.BatchCommands)]
public sealed class IngestionBatchesController(
    RegisterIngestionBatchService registrationService,
    ReadIngestionBatchService readService,
    PrepareIngestionUploadService prepareUploadService,
    CompleteIngestionUploadService completeUploadService) : ControllerBase
{
    [HttpPost(Name = IngestionOperationIds.RegisterBatch)]
    [Authorize(Policy = IngestionAuthorizationPolicies.Upload)]
    [ProducesResponseType<IngestionBatchRegistrationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<IngestionBatchRegistrationResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IngestionBatchRegistrationResponse>> RegisterAsync(
        [FromBody] RegisterIngestionBatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Manifest);
        var result = await registrationService.RegisterAsync(
            new RegisterIngestionBatchCommand(
                request.Manifest,
                request.ManifestDigest,
                RequireIdempotencyKey(Request),
                IngestionServiceIdentityAccessor.Require(User)),
            cancellationToken);
        var response = new IngestionBatchRegistrationResponse(
            IngestionBatchContractMapper.ToDto(result.Batch),
            result.Replayed);
        if (result.Replayed)
        {
            return Ok(response);
        }

        return CreatedAtRoute(
            IngestionOperationIds.GetBatch,
            new { batchId = response.Batch.Id },
            response);
    }

    [HttpGet("{batchId:guid}", Name = IngestionOperationIds.GetBatch)]
    [Authorize(Policy = IngestionAuthorizationPolicies.Read)]
    [ProducesResponseType<IngestionBatchDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IngestionBatchDto>> GetAsync(
        Guid batchId,
        CancellationToken cancellationToken)
    {
        var batch = await readService.ReadAsync(batchId, cancellationToken);
        if (batch is null)
        {
            throw new IngestionApplicationException(
                "Ingestion.Batches",
                "INGESTION_BATCH_NOT_FOUND",
                StatusCodes.Status404NotFound,
                $"Import batch '{batchId:D}' was not found.",
                "Use the exact ImportBatchId returned by registration.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["batchId"] = batchId,
                });
        }

        return Ok(batch);
    }

    [HttpPost("{batchId:guid}/upload-request", Name = IngestionOperationIds.PrepareBatchUpload)]
    [Authorize(Policy = IngestionAuthorizationPolicies.Upload)]
    [ProducesResponseType<IngestionUploadAuthorizationDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IngestionUploadAuthorizationDto>> PrepareUploadAsync(
        Guid batchId,
        [FromBody] PrepareIngestionUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await prepareUploadService.PrepareAsync(
            new PrepareIngestionUploadCommand(
                batchId,
                request.ExpectedAggregateRevision,
                RequireIdempotencyKey(Request),
                IngestionServiceIdentityAccessor.Require(User)),
            cancellationToken));
    }

    [HttpPost("{batchId:guid}/upload-complete", Name = IngestionOperationIds.CompleteBatchUpload)]
    [Authorize(Policy = IngestionAuthorizationPolicies.Upload)]
    [ProducesResponseType<IngestionBatchCommandResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IngestionBatchCommandResponse>> CompleteUploadAsync(
        Guid batchId,
        [FromBody] CompleteIngestionUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await completeUploadService.CompleteAsync(
            new CompleteIngestionUploadCommand(
                batchId,
                request.ExpectedAggregateRevision,
                RequireIdempotencyKey(Request),
                IngestionServiceIdentityAccessor.Require(User)),
            cancellationToken));
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
                "Retry with one stable Idempotency-Key for this command.");
        }

        return values[0]!;
    }
}
