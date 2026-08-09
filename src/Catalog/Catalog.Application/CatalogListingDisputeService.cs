using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>Owns Catalog listing dispute commands without mutating Query or Promotion state directly.</summary>
public sealed class CatalogListingDisputeService(
    ICatalogListingDisputeRepository repository,
    ICatalogIdSource idSource,
    TimeProvider timeProvider)
{
    public async Task<CatalogListingDisputeResponse> OpenAsync(
        Guid listingId,
        OpenCatalogListingDisputeRequest request,
        CatalogActor actor,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken)
    {
        RequireListingId(listingId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(eventContext);
        if (request.ExpectedListingVersion <= 0)
        {
            throw ContractFailure(
                "catalog.listing_dispute_expected_listing_version_invalid",
                "Expected listing version must be greater than zero.");
        }

        var dispute = ListingDispute.Open(
            idSource.CreateId(),
            listingId,
            request.Reason,
            actor.Id,
            timeProvider.GetUtcNow());
        var stored = await repository.AddAsync(
            dispute,
            request.ExpectedListingVersion,
            eventContext,
            cancellationToken);
        return ToResponse(stored);
    }

    public async Task<CatalogListingDisputeResponse> ResolveAsync(
        Guid listingId,
        Guid disputeId,
        ResolveCatalogListingDisputeRequest request,
        CatalogActor actor,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken)
    {
        RequireListingId(listingId);
        if (disputeId == Guid.Empty)
        {
            throw ContractFailure(
                "catalog.listing_dispute_id_invalid",
                "Listing dispute ID must be a non-empty UUID.");
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(eventContext);
        if (request.ExpectedDisputeRevision <= 0)
        {
            throw ContractFailure(
                "catalog.listing_dispute_expected_revision_invalid",
                "Expected dispute revision must be greater than zero.");
        }

        var dispute = await repository.GetAsync(
            listingId,
            disputeId,
            cancellationToken)
            ?? throw new CatalogNotFoundException("listing-dispute", disputeId);
        var storedAggregateRevision = dispute.AggregateRevision;
        dispute.Resolve(
            request.ExpectedDisputeRevision,
            actor.Id,
            request.ResolutionReason,
            timeProvider.GetUtcNow());
        var stored = await repository.SaveAsync(
            dispute,
            storedAggregateRevision,
            eventContext,
            cancellationToken);
        return ToResponse(stored);
    }

    private static CatalogListingDisputeResponse ToResponse(ListingDispute dispute) =>
        new(
            dispute.Id,
            dispute.ListingId,
            dispute.State switch
            {
                ListingDisputeState.Open => ListingDisputeStateContract.Open,
                ListingDisputeState.Resolved => ListingDisputeStateContract.Resolved,
                _ => throw ContractFailure(
                    "catalog.listing_dispute_state_unsupported",
                    $"Listing dispute state '{dispute.State}' is unsupported."),
            },
            dispute.BlocksPromotion,
            dispute.OpenReason,
            dispute.OpenedByActorId,
            dispute.OpenedAtUtc,
            dispute.ResolutionReason,
            dispute.ResolvedByActorId,
            dispute.ResolvedAtUtc,
            dispute.AggregateRevision);

    private static void RequireListingId(Guid listingId)
    {
        if (listingId == Guid.Empty)
        {
            throw ContractFailure(
                "catalog.listing_id_invalid",
                "Listing ID must be a non-empty UUID.");
        }
    }

    private static CatalogContractException ContractFailure(
        string code,
        string message) =>
        new(code, message);
}
