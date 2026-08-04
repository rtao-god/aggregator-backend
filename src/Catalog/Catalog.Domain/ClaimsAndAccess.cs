using System.Collections.ObjectModel;

namespace Aggregator.Catalog.Domain;

public enum ClaimState
{
    Pending = 1,
    Verified = 2,
    Rejected = 3,
    Revoked = 4,
}

public enum ListingAccessScope
{
    ReadDraft = 1,
    ProposeRevision = 2,
    ManageContacts = 3,
    ManageMedia = 4,
}

public sealed class ListingClaim
{
    private ListingClaim(
        Guid id,
        Guid listingId,
        Guid claimantActorId,
        ClaimState state,
        string evidenceReference,
        string evidenceDigest,
        DateTimeOffset submittedAtUtc,
        Guid? decidedByActorId,
        DateTimeOffset? decidedAtUtc,
        string? decisionReason)
    {
        Id = id;
        ListingId = listingId;
        ClaimantActorId = claimantActorId;
        State = state;
        EvidenceReference = evidenceReference;
        EvidenceDigest = evidenceDigest;
        SubmittedAtUtc = submittedAtUtc;
        DecidedByActorId = decidedByActorId;
        DecidedAtUtc = decidedAtUtc;
        DecisionReason = decisionReason;
    }

    public Guid Id { get; }

    public Guid ListingId { get; }

    public Guid ClaimantActorId { get; }

    public ClaimState State { get; private set; }

    public string EvidenceReference { get; }

    public string EvidenceDigest { get; }

    public DateTimeOffset SubmittedAtUtc { get; }

    public Guid? DecidedByActorId { get; private set; }

    public DateTimeOffset? DecidedAtUtc { get; private set; }

    public string? DecisionReason { get; private set; }

    public static ListingClaim Submit(
        Guid id,
        Guid listingId,
        Guid claimantActorId,
        string evidenceReference,
        string evidenceDigest,
        DateTimeOffset submittedAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Claim ID is required.", nameof(id));
        }

        if (listingId == Guid.Empty)
        {
            throw new ArgumentException("Listing ID is required.", nameof(listingId));
        }

        if (claimantActorId == Guid.Empty)
        {
            throw new ArgumentException("Claimant actor ID is required.", nameof(claimantActorId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        CatalogClock.RequireUtc(submittedAtUtc, nameof(submittedAtUtc));
        return new ListingClaim(
            id,
            listingId,
            claimantActorId,
            ClaimState.Pending,
            evidenceReference.Trim(),
            CatalogDigest.RequireSha256(evidenceDigest, nameof(evidenceDigest)),
            submittedAtUtc,
            decidedByActorId: null,
            decidedAtUtc: null,
            decisionReason: null);
    }

    public static ListingClaim Restore(ListingClaimSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Id == Guid.Empty || snapshot.ListingId == Guid.Empty || snapshot.ClaimantActorId == Guid.Empty)
        {
            throw new ArgumentException("Claim snapshot is invalid.", nameof(snapshot));
        }

        return new ListingClaim(
            snapshot.Id,
            snapshot.ListingId,
            snapshot.ClaimantActorId,
            snapshot.State,
            snapshot.EvidenceReference,
            snapshot.EvidenceDigest,
            snapshot.SubmittedAtUtc,
            snapshot.DecidedByActorId,
            snapshot.DecidedAtUtc,
            snapshot.DecisionReason);
    }

    public ListingAccessGrant Verify(
        Guid grantId,
        Guid reviewerActorId,
        IEnumerable<ListingAccessScope> scopes,
        DateTimeOffset verifiedAtUtc,
        DateTimeOffset? expiresAtUtc)
    {
        EnsurePending();
        if (reviewerActorId == Guid.Empty)
        {
            throw new ArgumentException("Reviewer actor ID is required.", nameof(reviewerActorId));
        }

        ArgumentNullException.ThrowIfNull(scopes);
        CatalogClock.RequireUtc(verifiedAtUtc, nameof(verifiedAtUtc));
        if (expiresAtUtc is not null)
        {
            CatalogClock.RequireUtc(expiresAtUtc.Value, nameof(expiresAtUtc));
            if (expiresAtUtc <= verifiedAtUtc)
            {
                throw new ArgumentException("Grant expiration must be after verification.", nameof(expiresAtUtc));
            }
        }

        State = ClaimState.Verified;
        DecidedByActorId = reviewerActorId;
        DecidedAtUtc = verifiedAtUtc;
        DecisionReason = null;
        return ListingAccessGrant.Create(
            grantId,
            ListingId,
            ClaimantActorId,
            scopes,
            verifiedAtUtc,
            expiresAtUtc,
            claimId: Id);
    }

    public void Reject(Guid reviewerActorId, string reason, DateTimeOffset rejectedAtUtc)
    {
        EnsurePending();
        if (reviewerActorId == Guid.Empty)
        {
            throw new ArgumentException("Reviewer actor ID is required.", nameof(reviewerActorId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        CatalogClock.RequireUtc(rejectedAtUtc, nameof(rejectedAtUtc));
        State = ClaimState.Rejected;
        DecidedByActorId = reviewerActorId;
        DecidedAtUtc = rejectedAtUtc;
        DecisionReason = reason.Trim();
    }

    public void Revoke(Guid reviewerActorId, string reason, DateTimeOffset revokedAtUtc)
    {
        if (State != ClaimState.Verified)
        {
            throw new CatalogInvariantException("Only a verified claim can be revoked.");
        }

        if (reviewerActorId == Guid.Empty)
        {
            throw new ArgumentException("Reviewer actor ID is required.", nameof(reviewerActorId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        CatalogClock.RequireUtc(revokedAtUtc, nameof(revokedAtUtc));
        State = ClaimState.Revoked;
        DecidedByActorId = reviewerActorId;
        DecidedAtUtc = revokedAtUtc;
        DecisionReason = reason.Trim();
    }

    public ListingClaimSnapshot ToSnapshot() =>
        new(
            Id,
            ListingId,
            ClaimantActorId,
            State,
            EvidenceReference,
            EvidenceDigest,
            SubmittedAtUtc,
            DecidedByActorId,
            DecidedAtUtc,
            DecisionReason);

    private void EnsurePending()
    {
        if (State != ClaimState.Pending)
        {
            throw new CatalogInvariantException($"Claim '{Id}' is already in state '{State}'.");
        }
    }
}

public sealed record ListingClaimSnapshot(
    Guid Id,
    Guid ListingId,
    Guid ClaimantActorId,
    ClaimState State,
    string EvidenceReference,
    string EvidenceDigest,
    DateTimeOffset SubmittedAtUtc,
    Guid? DecidedByActorId,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionReason);

public sealed record ListingAccessGrant
{
    private ListingAccessGrant(
        Guid id,
        Guid listingId,
        Guid actorId,
        IReadOnlySet<ListingAccessScope> scopes,
        DateTimeOffset grantedAtUtc,
        DateTimeOffset? expiresAtUtc,
        Guid claimId,
        DateTimeOffset? revokedAtUtc,
        Guid? revokedByActorId,
        string? revocationReason)
    {
        Id = id;
        ListingId = listingId;
        ActorId = actorId;
        Scopes = scopes;
        GrantedAtUtc = grantedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        ClaimId = claimId;
        RevokedAtUtc = revokedAtUtc;
        RevokedByActorId = revokedByActorId;
        RevocationReason = revocationReason;
    }

    public Guid Id { get; }

    public Guid ListingId { get; }

    public Guid ActorId { get; }

    public IReadOnlySet<ListingAccessScope> Scopes { get; }

    public DateTimeOffset GrantedAtUtc { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public Guid ClaimId { get; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public Guid? RevokedByActorId { get; private set; }

    public string? RevocationReason { get; private set; }

    public bool IsActiveAt(DateTimeOffset timestampUtc)
    {
        CatalogClock.RequireUtc(timestampUtc, nameof(timestampUtc));
        return RevokedAtUtc is null && (ExpiresAtUtc is null || timestampUtc < ExpiresAtUtc);
    }

    public void EnsureScope(ListingAccessScope scope, DateTimeOffset timestampUtc)
    {
        if (!IsActiveAt(timestampUtc) || !Scopes.Contains(scope))
        {
            throw new CatalogAuthorizationException(ActorId, ListingId, scope);
        }
    }

    public ListingAccessGrant Revoke(Guid actorId, string reason, DateTimeOffset revokedAtUtc)
    {
        if (RevokedAtUtc is not null)
        {
            throw new CatalogInvariantException($"Access grant '{Id}' is already revoked.");
        }

        if (actorId == Guid.Empty)
        {
            throw new ArgumentException("Revoking actor ID is required.", nameof(actorId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        CatalogClock.RequireUtc(revokedAtUtc, nameof(revokedAtUtc));
        if (revokedAtUtc < GrantedAtUtc)
        {
            throw new ArgumentException("Revocation cannot precede the grant.", nameof(revokedAtUtc));
        }

        RevokedAtUtc = revokedAtUtc;
        RevokedByActorId = actorId;
        RevocationReason = reason.Trim();
        return this;
    }

    internal static ListingAccessGrant Create(
        Guid id,
        Guid listingId,
        Guid actorId,
        IEnumerable<ListingAccessScope> scopes,
        DateTimeOffset grantedAtUtc,
        DateTimeOffset? expiresAtUtc,
        Guid claimId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Grant ID is required.", nameof(id));
        }

        if (listingId == Guid.Empty)
        {
            throw new ArgumentException("Listing ID is required.", nameof(listingId));
        }

        if (actorId == Guid.Empty)
        {
            throw new ArgumentException("Actor ID is required.", nameof(actorId));
        }

        if (claimId == Guid.Empty)
        {
            throw new ArgumentException("Claim ID is required.", nameof(claimId));
        }

        ArgumentNullException.ThrowIfNull(scopes);
        CatalogClock.RequireUtc(grantedAtUtc, nameof(grantedAtUtc));
        var scopeSet = scopes.ToHashSet();
        if (scopeSet.Count == 0)
        {
            throw new ArgumentException("A listing access grant must contain at least one scope.", nameof(scopes));
        }

        return new ListingAccessGrant(
            id,
            listingId,
            actorId,
            new ReadOnlySet<ListingAccessScope>(scopeSet),
            grantedAtUtc,
            expiresAtUtc,
            claimId,
            revokedAtUtc: null,
            revokedByActorId: null,
            revocationReason: null);
    }
}

public sealed class CatalogAuthorizationException : InvalidOperationException
{
    public CatalogAuthorizationException(Guid actorId, Guid listingId, ListingAccessScope requiredScope)
        : base($"Actor '{actorId}' lacks active scope '{requiredScope}' for listing '{listingId}'.")
    {
        ActorId = actorId;
        ListingId = listingId;
        RequiredScope = requiredScope;
    }

    public Guid ActorId { get; }

    public Guid ListingId { get; }

    public ListingAccessScope RequiredScope { get; }
}
