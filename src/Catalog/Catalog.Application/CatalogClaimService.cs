using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public sealed class CatalogClaimService(
    ICatalogRepository repository,
    ICatalogIdSource idSource,
    TimeProvider timeProvider)
{
    public async Task<ListingClaimResponse> SubmitAsync(
        SubmitListingClaimRequest request,
        CatalogActor claimant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(claimant);
        _ = await repository.GetListingAsync(request.ListingId, cancellationToken)
            ?? throw new CatalogNotFoundException("listing", request.ListingId);

        var claim = ListingClaim.Submit(
            idSource.CreateId(),
            request.ListingId,
            claimant.Id,
            request.EvidenceReference,
            request.EvidenceDigest,
            timeProvider.GetUtcNow());
        await repository.AddClaimAsync(claim, cancellationToken);
        return CatalogContractMapper.ToResponse(claim);
    }

    public async Task<ListingAccessGrantResponse> VerifyAsync(
        Guid claimId,
        VerifyListingClaimRequest request,
        CatalogActor reviewer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reviewer);
        ArgumentNullException.ThrowIfNull(request.Scopes);
        var claim = await RequireClaimAsync(claimId, cancellationToken);
        var verifiedAtUtc = timeProvider.GetUtcNow();
        var grant = claim.Verify(
            idSource.CreateId(),
            reviewer.Id,
            request.Scopes.Select(CatalogContractMapper.ToDomain),
            verifiedAtUtc,
            request.ExpiresAtUtc);
        var integrationEvent = new CatalogListingClaimVerifiedV1(
            idSource.CreateId(),
            claim.Id,
            grant.Id,
            grant.ListingId,
            grant.ActorId,
            grant.Scopes.Select(scope => (ListingAccessScopeContract)scope).Order().ToArray(),
            grant.ExpiresAtUtc,
            verifiedAtUtc);
        var outboxMessage = new CatalogOutboxMessage(
            integrationEvent.EventId,
            CatalogIntegrationEventTypes.ListingClaimVerifiedV1,
            EventRevision: 1,
            CatalogCanonicalJson.SerializeEvent(integrationEvent),
            verifiedAtUtc);
        await repository.CompleteClaimVerificationAsync(
            claim,
            grant,
            outboxMessage,
            cancellationToken);
        return CatalogContractMapper.ToResponse(grant);
    }

    public async Task<ListingClaimResponse> RejectAsync(
        Guid claimId,
        RejectListingClaimRequest request,
        CatalogActor reviewer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reviewer);
        var claim = await RequireClaimAsync(claimId, cancellationToken);
        claim.Reject(reviewer.Id, request.Reason, timeProvider.GetUtcNow());
        await repository.SaveClaimDecisionAsync(claim, outboxMessage: null, cancellationToken);
        return CatalogContractMapper.ToResponse(claim);
    }

    public async Task<ListingClaimResponse> RevokeAsync(
        Guid claimId,
        RevokeListingClaimRequest request,
        CatalogActor reviewer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reviewer);
        var claim = await RequireClaimAsync(claimId, cancellationToken);
        var revokedAtUtc = timeProvider.GetUtcNow();
        claim.Revoke(reviewer.Id, request.Reason, revokedAtUtc);
        var integrationEvent = new CatalogListingClaimRevokedV1(
            idSource.CreateId(),
            claim.Id,
            claim.ListingId,
            claim.ClaimantActorId,
            revokedAtUtc);
        var outboxMessage = new CatalogOutboxMessage(
            integrationEvent.EventId,
            CatalogIntegrationEventTypes.ListingClaimRevokedV1,
            EventRevision: 1,
            CatalogCanonicalJson.SerializeEvent(integrationEvent),
            revokedAtUtc);
        await repository.SaveClaimDecisionAsync(claim, outboxMessage, cancellationToken);
        return CatalogContractMapper.ToResponse(claim);
    }

    private async Task<ListingClaim> RequireClaimAsync(
        Guid claimId,
        CancellationToken cancellationToken)
    {
        if (claimId == Guid.Empty)
        {
            throw new ArgumentException("Claim ID is required.", nameof(claimId));
        }

        return await repository.GetClaimAsync(claimId, cancellationToken)
            ?? throw new CatalogNotFoundException("listing-claim", claimId);
    }
}
