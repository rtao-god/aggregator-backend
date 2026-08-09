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

    public async Task<ListingAccessGrant?> GetByClaimAsync(
        Guid claimId,
        CancellationToken cancellationToken)
    {
        if (claimId == Guid.Empty)
        {
            throw new ArgumentException("Claim ID is required.", nameof(claimId));
        }

        var row = await _dbContext.ListingAccessGrants
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.ClaimId == claimId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var scopeValues = await _dbContext.ListingAccessScopes
            .AsNoTracking()
            .Where(candidate => candidate.GrantId == row.Id)
            .OrderBy(candidate => candidate.Scope)
            .Select(candidate => candidate.Scope)
            .ToArrayAsync(cancellationToken);
        var scopes = scopeValues
            .Select(value => RequireEnum<ListingAccessScope>(
                value,
                "listing access scope"))
            .ToHashSet();
        return RehydrateAccessGrant(row, scopes);
    }

    public Task CompleteVerificationAsync(
        ListingClaim claim,
        ListingAccessGrant grant,
        CatalogOutboxMessage claimOutboxMessage,
        CatalogOutboxMessage grantOutboxMessage,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            ArgumentNullException.ThrowIfNull(claim);
            ArgumentNullException.ThrowIfNull(grant);
            ArgumentNullException.ThrowIfNull(claimOutboxMessage);
            ArgumentNullException.ThrowIfNull(grantOutboxMessage);
            var claimRow = await RequireTrackedClaimAsync(claim.Id, innerCancellationToken);
            if (claimRow.State != (int)ClaimState.Pending)
            {
                throw new CatalogConflictException(
                    $"Claim '{claim.Id}' is no longer pending.");
            }

            ApplyClaimMutation(claimRow, claim);
            _dbContext.ListingAccessGrants.Add(ToRow(grant));
            foreach (var scope in grant.Scopes)
            {
                _dbContext.ListingAccessScopes.Add(new CatalogListingAccessScopeRow
                {
                    GrantId = grant.Id,
                    Scope = (int)scope,
                });
            }

            AddOutbox(claimOutboxMessage);
            AddOutbox(grantOutboxMessage);
            await _dbContext.SaveChangesAsync(innerCancellationToken);
        }, cancellationToken);

    public Task CompleteRevocationAsync(
        ListingClaim claim,
        ListingAccessGrant grant,
        CatalogOutboxMessage claimOutboxMessage,
        CatalogOutboxMessage grantOutboxMessage,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            ArgumentNullException.ThrowIfNull(claim);
            ArgumentNullException.ThrowIfNull(grant);
            ArgumentNullException.ThrowIfNull(claimOutboxMessage);
            ArgumentNullException.ThrowIfNull(grantOutboxMessage);
            var claimRow = await RequireTrackedClaimAsync(claim.Id, innerCancellationToken);
            if (claimRow.State != (int)ClaimState.Verified)
            {
                throw new CatalogConflictException(
                    $"Claim '{claim.Id}' is no longer verified.");
            }

            var grantRow = await RequireTrackedAccessGrantAsync(
                grant.Id,
                claim.Id,
                innerCancellationToken);
            var expectedPreviousRevision = checked(grant.AggregateRevision - 1);
            if (grantRow.AggregateRevision != expectedPreviousRevision ||
                grantRow.RevokedAtUtc is not null)
            {
                throw new CatalogConflictException(
                    $"Access grant '{grant.Id}' expected revision '{expectedPreviousRevision}' but is at '{grantRow.AggregateRevision}'.");
            }

            ApplyClaimMutation(claimRow, claim);
            ApplyGrantMutation(grantRow, grant);
            AddOutbox(claimOutboxMessage);
            AddOutbox(grantOutboxMessage);
            await _dbContext.SaveChangesAsync(innerCancellationToken);
        }, cancellationToken);

    public Task CompleteClaimVerificationAsync(
        ListingClaim claim,
        ListingAccessGrant grant,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken) =>
        throw new CatalogConflictException(
            "The legacy single-event claim verification path is disabled.");

    public Task SaveClaimDecisionAsync(
        ListingClaim claim,
        CatalogOutboxMessage? outboxMessage,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            ArgumentNullException.ThrowIfNull(claim);
            if (claim.State != ClaimState.Rejected || outboxMessage is not null)
            {
                throw new CatalogConflictException(
                    "Claim decision persistence accepts only a rejection without cross-context effects.");
            }

            var claimRow = await RequireTrackedClaimAsync(claim.Id, innerCancellationToken);
            if (claimRow.State != (int)ClaimState.Pending)
            {
                throw new CatalogConflictException(
                    $"Claim '{claim.Id}' is no longer pending.");
            }

            ApplyClaimMutation(claimRow, claim);
            await _dbContext.SaveChangesAsync(innerCancellationToken);
        }, cancellationToken);

    private async Task<CatalogListingClaimRow> RequireTrackedClaimAsync(
        Guid claimId,
        CancellationToken cancellationToken) =>
        await _dbContext.ListingClaims.SingleOrDefaultAsync(row => row.Id == claimId, cancellationToken)
            ?? throw new CatalogNotFoundException("listing-claim", claimId);

    private async Task<CatalogListingAccessGrantRow> RequireTrackedAccessGrantAsync(
        Guid grantId,
        Guid claimId,
        CancellationToken cancellationToken) =>
        await _dbContext.ListingAccessGrants.SingleOrDefaultAsync(
            row => row.Id == grantId && row.ClaimId == claimId,
            cancellationToken)
        ?? throw new CatalogNotFoundException("listing-access-grant", grantId);

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

    private static CatalogListingAccessGrantRow ToRow(ListingAccessGrant grant) =>
        new()
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

    private static ListingAccessGrant RehydrateAccessGrant(
        CatalogListingAccessGrantRow row,
        IReadOnlySet<ListingAccessScope> scopes) =>
        ListingAccessGrant.Restore(new ListingAccessGrantSnapshot(
            row.Id,
            row.ListingId,
            row.ActorId,
            scopes,
            row.GrantedAtUtc,
            row.ExpiresAtUtc,
            row.ClaimId,
            row.RevokedAtUtc,
            row.RevokedByActorId,
            row.RevocationReason,
            row.AggregateRevision));

    private static void ApplyClaimMutation(CatalogListingClaimRow row, ListingClaim claim)
    {
        row.State = (int)claim.State;
        row.DecidedByActorId = claim.DecidedByActorId;
        row.DecidedAtUtc = claim.DecidedAtUtc;
        row.DecisionReason = claim.DecisionReason;
    }

    private static void ApplyGrantMutation(
        CatalogListingAccessGrantRow row,
        ListingAccessGrant grant)
    {
        row.RevokedAtUtc = grant.RevokedAtUtc;
        row.RevokedByActorId = grant.RevokedByActorId;
        row.RevocationReason = grant.RevocationReason;
        row.AggregateRevision = grant.AggregateRevision;
    }
}
