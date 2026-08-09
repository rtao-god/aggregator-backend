using Aggregator.Catalog.Application;
using Aggregator.Catalog.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Catalog.Infrastructure;

public sealed partial class EfCatalogRepository
{
    public async Task AddClaimAsync(ListingClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var activeClaimExists = await _dbContext.ListingClaims
            .AsNoTracking()
            .AnyAsync(
                row => row.ListingId == claim.ListingId &&
                       row.ClaimantActorId == claim.ClaimantActorId &&
                       (row.State == (int)ClaimState.Pending || row.State == (int)ClaimState.Verified),
                cancellationToken);
        if (activeClaimExists)
        {
            throw new CatalogConflictException(
                $"Actor '{claim.ClaimantActorId}' already has an active claim for listing '{claim.ListingId}'.");
        }

        _dbContext.ListingClaims.Add(ToRow(claim));
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ListingClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken)
    {
        var row = await _dbContext.ListingClaims
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == claimId, cancellationToken);
        return row is null ? null : RehydrateClaim(row);
    }

    public Task CompleteClaimVerificationAsync(
        ListingClaim claim,
        ListingAccessGrant grant,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            ArgumentNullException.ThrowIfNull(claim);
            ArgumentNullException.ThrowIfNull(grant);
            ArgumentNullException.ThrowIfNull(outboxMessage);
            var claimRow = await RequireTrackedClaimAsync(claim.Id, innerCancellationToken);
            if (claimRow.State != (int)ClaimState.Pending)
            {
                throw new CatalogConflictException(
                    $"Claim '{claim.Id}' is no longer pending.");
            }

            ApplyClaimMutation(claimRow, claim);
            _dbContext.ListingAccessGrants.Add(new CatalogListingAccessGrantRow
            {
                Id = grant.Id,
                ListingId = grant.ListingId,
                ActorId = grant.ActorId,
                GrantedAtUtc = grant.GrantedAtUtc,
                ExpiresAtUtc = grant.ExpiresAtUtc,
                ClaimId = grant.ClaimId,
                RevokedAtUtc = grant.RevokedAtUtc,
                RevokedByActorId = grant.RevokedByActorId,
                RevocationReason = grant.RevocationReason,
                AggregateRevision = grant.AggregateRevision,
            });
            foreach (var scope in grant.Scopes)
            {
                _dbContext.ListingAccessScopes.Add(new CatalogListingAccessScopeRow
                {
                    GrantId = grant.Id,
                    Scope = (int)scope,
                });
            }

            AddOutbox(outboxMessage);
            await _dbContext.SaveChangesAsync(innerCancellationToken);
        }, cancellationToken);

    public Task SaveClaimDecisionAsync(
        ListingClaim claim,
        CatalogOutboxMessage? outboxMessage,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            ArgumentNullException.ThrowIfNull(claim);
            var claimRow = await RequireTrackedClaimAsync(claim.Id, innerCancellationToken);
            var expectedPreviousState = claim.State switch
            {
                ClaimState.Rejected => ClaimState.Pending,
                ClaimState.Revoked => ClaimState.Verified,
                _ => throw new CatalogConflictException(
                    $"Claim state '{claim.State}' is not a persisted decision transition."),
            };
            if (claimRow.State != (int)expectedPreviousState)
            {
                throw new CatalogConflictException(
                    $"Claim '{claim.Id}' expected state '{expectedPreviousState}' but is at '{RequireEnum<ClaimState>(claimRow.State, "claim state")}'.");
            }

            ApplyClaimMutation(claimRow, claim);
            if (claim.State == ClaimState.Revoked)
            {
                var grants = await _dbContext.ListingAccessGrants
                    .Where(row => row.ClaimId == claim.Id && row.RevokedAtUtc == null)
                    .ToArrayAsync(innerCancellationToken);
                foreach (var grant in grants)
                {
                    grant.RevokedAtUtc = claim.DecidedAtUtc;
                    grant.RevokedByActorId = claim.DecidedByActorId;
                    grant.RevocationReason = claim.DecisionReason;
                    grant.AggregateRevision = checked(grant.AggregateRevision + 1);
                }
            }

            if (outboxMessage is not null)
            {
                AddOutbox(outboxMessage);
            }

            await _dbContext.SaveChangesAsync(innerCancellationToken);
        }, cancellationToken);

    private async Task<CatalogListingClaimRow> RequireTrackedClaimAsync(
        Guid claimId,
        CancellationToken cancellationToken) =>
        await _dbContext.ListingClaims.SingleOrDefaultAsync(row => row.Id == claimId, cancellationToken)
            ?? throw new CatalogNotFoundException("listing-claim", claimId);

    private static CatalogListingClaimRow ToRow(ListingClaim claim) =>
        new()
        {
            Id = claim.Id,
            ListingId = claim.ListingId,
            ClaimantActorId = claim.ClaimantActorId,
            State = (int)claim.State,
            EvidenceReference = claim.EvidenceReference,
            EvidenceDigest = claim.EvidenceDigest,
            SubmittedAtUtc = claim.SubmittedAtUtc,
            DecidedByActorId = claim.DecidedByActorId,
            DecidedAtUtc = claim.DecidedAtUtc,
            DecisionReason = claim.DecisionReason,
        };

    private static ListingClaim RehydrateClaim(CatalogListingClaimRow row) =>
        ListingClaim.Restore(new ListingClaimSnapshot(
            row.Id,
            row.ListingId,
            row.ClaimantActorId,
            RequireEnum<ClaimState>(row.State, "claim state"),
            row.EvidenceReference,
            row.EvidenceDigest,
            row.SubmittedAtUtc,
            row.DecidedByActorId,
            row.DecidedAtUtc,
            row.DecisionReason));

    private static void ApplyClaimMutation(CatalogListingClaimRow row, ListingClaim claim)
    {
        row.State = (int)claim.State;
        row.DecidedByActorId = claim.DecidedByActorId;
        row.DecidedAtUtc = claim.DecidedAtUtc;
        row.DecisionReason = claim.DecisionReason;
    }
}
