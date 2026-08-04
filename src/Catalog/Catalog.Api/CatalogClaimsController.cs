using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Catalog.Api;

[ApiController]
[Route("api/catalog-command/claims")]
[EnableRateLimiting(CatalogRateLimitPolicies.Command)]
public sealed class CatalogClaimsController(CatalogClaimService service) : ControllerBase
{
    [HttpPost(Name = CatalogOperationIds.SubmitClaim)]
    [Authorize(Policy = CatalogAuthorizationPolicies.SubmitClaim)]
    [ProducesResponseType<ListingClaimResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ListingClaimResponse>> SubmitAsync(
        [FromBody] SubmitListingClaimRequest request,
        CancellationToken cancellationToken)
    {
        var response = await service.SubmitAsync(
            request,
            CatalogActorAccessor.Require(HttpContext),
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("{claimId:guid}/decisions/verify", Name = CatalogOperationIds.VerifyClaim)]
    [Authorize(Policy = CatalogAuthorizationPolicies.VerifyClaim)]
    [ProducesResponseType<ListingAccessGrantResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ListingAccessGrantResponse>> VerifyAsync(
        Guid claimId,
        [FromBody] VerifyListingClaimRequest request,
        CancellationToken cancellationToken) =>
        Ok(await service.VerifyAsync(
            claimId,
            request,
            CatalogActorAccessor.Require(HttpContext),
            cancellationToken));

    [HttpPost("{claimId:guid}/decisions/reject", Name = CatalogOperationIds.RejectClaim)]
    [Authorize(Policy = CatalogAuthorizationPolicies.VerifyClaim)]
    [ProducesResponseType<ListingClaimResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ListingClaimResponse>> RejectAsync(
        Guid claimId,
        [FromBody] RejectListingClaimRequest request,
        CancellationToken cancellationToken) =>
        Ok(await service.RejectAsync(
            claimId,
            request,
            CatalogActorAccessor.Require(HttpContext),
            cancellationToken));

    [HttpPost("{claimId:guid}/decisions/revoke", Name = CatalogOperationIds.RevokeClaim)]
    [Authorize(Policy = CatalogAuthorizationPolicies.VerifyClaim)]
    [ProducesResponseType<ListingClaimResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ListingClaimResponse>> RevokeAsync(
        Guid claimId,
        [FromBody] RevokeListingClaimRequest request,
        CancellationToken cancellationToken) =>
        Ok(await service.RevokeAsync(
            claimId,
            request,
            CatalogActorAccessor.Require(HttpContext),
            cancellationToken));
}
