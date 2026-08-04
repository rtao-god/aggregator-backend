using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Catalog.Api;

[ApiController]
[Route("api/catalog-command")]
[Authorize(Policy = CatalogAuthorizationPolicies.EditListing)]
[EnableRateLimiting(CatalogRateLimitPolicies.Command)]
public sealed class CatalogListingsController(CatalogListingService service) : ControllerBase
{
    [HttpPost("catalogs/{catalogKey}/listings", Name = CatalogOperationIds.CreateListing)]
    [ProducesResponseType<ListingResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ListingResponse>> CreateAsync(
        string catalogKey,
        [FromBody] CreateListingRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(catalogKey, request.CatalogKey, StringComparison.Ordinal))
        {
            throw new CatalogContractException(
                "catalog.listing_route_catalog_mismatch",
                "The route catalog key must match the request catalog key.");
        }

        var response = await service.CreateAsync(
            request,
            CatalogActorAccessor.Require(HttpContext),
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpGet("listings/{listingId:guid}", Name = CatalogOperationIds.GetListing)]
    [ProducesResponseType<ListingResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ListingResponse>> GetAsync(
        Guid listingId,
        CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(listingId, cancellationToken));

    [HttpPost("listings/{listingId:guid}/revisions", Name = CatalogOperationIds.CreateListingRevision)]
    [ProducesResponseType<ListingRevisionResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ListingRevisionResponse>> CreateRevisionAsync(
        Guid listingId,
        [FromBody] CreateListingRevisionRequest request,
        CancellationToken cancellationToken)
    {
        var response = await service.AddRevisionAsync(
            listingId,
            request,
            CatalogActorAccessor.Require(HttpContext),
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("listings/{listingId:guid}/approval-decisions/approve", Name = CatalogOperationIds.ApproveListingRevision)]
    [ProducesResponseType<EditorialDecisionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<EditorialDecisionResponse>> ApproveAsync(
        Guid listingId,
        [FromBody] ApproveListingRevisionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await service.ApproveAsync(
            listingId,
            request,
            CatalogActorAccessor.Require(HttpContext),
            cancellationToken));

    [HttpPost("listings/{listingId:guid}/approval-decisions/reject", Name = CatalogOperationIds.RejectListingRevision)]
    [ProducesResponseType<EditorialDecisionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<EditorialDecisionResponse>> RejectAsync(
        Guid listingId,
        [FromBody] RejectListingRevisionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await service.RejectAsync(
            listingId,
            request,
            CatalogActorAccessor.Require(HttpContext),
            cancellationToken));

    [HttpPost("listings/{listingId:guid}/archive", Name = CatalogOperationIds.ArchiveListing)]
    [ProducesResponseType<ListingResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ListingResponse>> ArchiveAsync(
        Guid listingId,
        [FromBody] ArchiveListingRequest request,
        CancellationToken cancellationToken) =>
        Ok(await service.ArchiveAsync(
            listingId,
            request,
            CatalogActorAccessor.Require(HttpContext),
            cancellationToken));
}
