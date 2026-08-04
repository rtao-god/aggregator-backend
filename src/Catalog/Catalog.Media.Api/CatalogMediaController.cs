using Aggregator.CatalogMedia.Application;
using Aggregator.CatalogMedia.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.CatalogMedia.Api;

[ApiController]
[Route("api/catalog-media/assets")]
public sealed class CatalogMediaController(CatalogMediaCommandService service) : ControllerBase
{
    [HttpPost(Name = CatalogMediaOperationIds.Register)]
    [Authorize(Policy = CatalogMediaAuthorizationPolicies.Manage)]
    [EnableRateLimiting(CatalogMediaRateLimitPolicies.Commands)]
    [ProducesResponseType<CatalogMediaResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<CatalogMediaResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CatalogMediaResponse>> RegisterAsync(
        [FromBody] RegisterCatalogMediaRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.RegisterAsync(
            request,
            CatalogMediaHttpContext.CreateCommandContext(HttpContext),
            CatalogMediaHttpContext.RequireIdempotencyKey(Request),
            cancellationToken);
        return result.Replayed
            ? Ok(result.Response)
            : StatusCode(StatusCodes.Status201Created, result.Response);
    }

    [HttpPost("{assetId:guid}/upload-authorizations", Name = CatalogMediaOperationIds.PrepareUpload)]
    [Authorize(Policy = CatalogMediaAuthorizationPolicies.Manage)]
    [EnableRateLimiting(CatalogMediaRateLimitPolicies.Commands)]
    [ProducesResponseType<CatalogMediaUploadAuthorizationResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CatalogMediaUploadAuthorizationResponse>> PrepareUploadAsync(
        Guid assetId,
        [FromBody] PrepareCatalogMediaUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.PrepareUploadAsync(
            assetId,
            request,
            CatalogMediaHttpContext.CreateCommandContext(HttpContext),
            CatalogMediaHttpContext.RequireIdempotencyKey(Request),
            cancellationToken);
        return Ok(result.Response);
    }

    [HttpPost("{assetId:guid}/upload-completions", Name = CatalogMediaOperationIds.CompleteUpload)]
    [Authorize(Policy = CatalogMediaAuthorizationPolicies.Manage)]
    [EnableRateLimiting(CatalogMediaRateLimitPolicies.Commands)]
    [ProducesResponseType<CatalogMediaResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CatalogMediaResponse>> CompleteUploadAsync(
        Guid assetId,
        [FromBody] CompleteCatalogMediaUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.CompleteUploadAsync(
            assetId,
            request,
            CatalogMediaHttpContext.CreateCommandContext(HttpContext),
            CatalogMediaHttpContext.RequireIdempotencyKey(Request),
            cancellationToken);
        return Ok(result.Response);
    }

    [HttpPost("{assetId:guid}/rights-revocations", Name = CatalogMediaOperationIds.RevokeRights)]
    [Authorize(Policy = CatalogMediaAuthorizationPolicies.RevokeRights)]
    [EnableRateLimiting(CatalogMediaRateLimitPolicies.Commands)]
    [ProducesResponseType<CatalogMediaResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CatalogMediaResponse>> RevokeRightsAsync(
        Guid assetId,
        [FromBody] RevokeCatalogMediaRightsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.RevokeRightsAsync(
            assetId,
            request,
            CatalogMediaHttpContext.CreateCommandContext(HttpContext),
            CatalogMediaHttpContext.RequireIdempotencyKey(Request),
            cancellationToken);
        return Ok(result.Response);
    }

    [HttpGet("{assetId:guid}", Name = CatalogMediaOperationIds.Get)]
    [Authorize(Policy = CatalogMediaAuthorizationPolicies.Read)]
    [EnableRateLimiting(CatalogMediaRateLimitPolicies.Reads)]
    [ProducesResponseType<CatalogMediaResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CatalogMediaResponse>> GetAsync(
        Guid assetId,
        CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(assetId, cancellationToken));
}
