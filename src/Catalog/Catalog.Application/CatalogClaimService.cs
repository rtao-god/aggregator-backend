using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public sealed class CatalogClaimService(
    ICatalogRepository repository,
    ICatalogListingAccessGrantRepository accessGrantRepository,
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

    /// <summary>Starts a new correlation root for a direct application or operator verification command.</summary>
    public Task<ListingAccessGrantResponse> VerifyAsync(
        Guid claimId,
        VerifyListingClaimRequest request,
        CatalogActor reviewer,
        CancellationToken cancellationToken) =>
        VerifyAsync(claimId, request, reviewer, CatalogEventContext.StartRoot(), cancellationToken);

    public async Task<ListingAccessGrantResponse> VerifyAsync(
        Guid claimId,
        VerifyListingClaimRequest request,
        CatalogActor reviewer,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reviewer);
        ArgumentNullException.ThrowIfNull(eventContext);
        ArgumentNullException.ThrowIfNull(request.Scopes);
        var claim = await RequireClaimAsync(claimId, cancellationToken);
        var verifiedAtUtc = timeProvider.GetUtcNow();
        var grant = claim.Verify(
            idSource.CreateId(),
            reviewer.Id,
            request.Scopes.Select(CatalogListingAccessGrantContractMapper.ToDomain),
            verifiedAtUtc,
            request.ExpiresAtUtc);
        var claimEvent = new CatalogListingClaimVerified(
            idSource.CreateId(),
            claim.Id,
            grant.Id,
            grant.ListingId,
            grant.ActorId,
            CatalogListingAccessGrantContractMapper.ToContracts(grant.Scopes),
            grant.ExpiresAtUtc,
            verifiedAtUtc);
        var claimOutboxMessage = CatalogOutboxMessageFactory.Create(
            claimEvent.EventId,
            CatalogIntegrationEventTypes.ListingClaimVerified,
            CatalogIntegrationEventContracts.ListingClaimVerified,
            claimEvent,
            verifiedAtUtc,
            eventContext);
        var grantOutboxMessage = CatalogListingAccessGrantEventFactory.Create(
            grant,
            idSource.CreateId(),
            verifiedAtUtc,
            eventContext);
        await accessGrantRepository.CompleteVerificationAsync(
            claim,
            grant,
            claimOutboxMessage,
            grantOutboxMessage,
            cancellationToken);
        return CatalogListingAccessGrantContractMapper.ToResponse(grant);
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
        await repository.SaveClaimDecisionAsync(
            claim,
            outboxMessage: null,
            cancellationToken);
        return CatalogContractMapper.ToResponse(claim);
    }

    /// <summary>Starts a new correlation root for a direct application or operator revocation command.</summary>
    public Task<ListingClaimResponse> RevokeAsync(
        Guid claimId,
        RevokeListingClaimRequest request,
        CatalogActor reviewer,
        CancellationToken cancellationToken) =>
        RevokeAsync(claimId, request, reviewer, CatalogEventContext.StartRoot(), cancellationToken);

    public async Task<ListingClaimResponse> RevokeAsync(
        Guid claimId,
        RevokeListingClaimRequest request,
        CatalogActor reviewer,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reviewer);
        ArgumentNullException.ThrowIfNull(eventContext);
        var claim = await RequireClaimAsync(claimId, cancellationToken);
        var grant = await accessGrantRepository.GetByClaimAsync(claim.Id, cancellationToken)
            ?? throw new CatalogConflictException(
                $"Verified claim '{claim.Id}' has no Catalog access grant.");
        var revokedAtUtc = timeProvider.GetUtcNow();
        claim.Revoke(reviewer.Id, request.Reason, revokedAtUtc);
        grant.Revoke(reviewer.Id, request.Reason, revokedAtUtc);
        var claimEvent = new CatalogListingClaimRevoked(
            idSource.CreateId(),
            claim.Id,
            claim.ListingId,
            claim.ClaimantActorId,
            revokedAtUtc);
        var claimOutboxMessage = CatalogOutboxMessageFactory.Create(
            claimEvent.EventId,
            CatalogIntegrationEventTypes.ListingClaimRevoked,
            CatalogIntegrationEventContracts.ListingClaimRevoked,
            claimEvent,
            revokedAtUtc,
            eventContext);
        var grantOutboxMessage = CatalogListingAccessGrantEventFactory.Create(
            grant,
            idSource.CreateId(),
            revokedAtUtc,
            eventContext);
        await accessGrantRepository.CompleteRevocationAsync(
            claim,
            grant,
            claimOutboxMessage,
            grantOutboxMessage,
            cancellationToken);
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
