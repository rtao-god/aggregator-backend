namespace Aggregator.Catalog.Domain;

/// <summary>Catalog-owned lifecycle for one listing dispute.</summary>
public enum ListingDisputeState
{
    Open = 1,
    Resolved = 2,
}

/// <summary>
/// Auditable dispute over one Catalog listing. An open dispute blocks paid promotion and new
/// publication selection until Catalog resolves it explicitly.
/// </summary>
public sealed class ListingDispute
{
    private ListingDispute(
        Guid id,
        Guid listingId,
        ListingDisputeState state,
        string openReason,
        Guid openedByActorId,
        DateTimeOffset openedAtUtc,
        string? resolutionReason,
        Guid? resolvedByActorId,
        DateTimeOffset? resolvedAtUtc,
        long aggregateRevision)
    {
        Id = id;
        ListingId = listingId;
        State = state;
        OpenReason = openReason;
        OpenedByActorId = openedByActorId;
        OpenedAtUtc = openedAtUtc;
        ResolutionReason = resolutionReason;
        ResolvedByActorId = resolvedByActorId;
        ResolvedAtUtc = resolvedAtUtc;
        AggregateRevision = aggregateRevision;
    }

    public Guid Id { get; }

    public Guid ListingId { get; }

    public ListingDisputeState State { get; private set; }

    public string OpenReason { get; }

    public Guid OpenedByActorId { get; }

    public DateTimeOffset OpenedAtUtc { get; }

    public string? ResolutionReason { get; private set; }

    public Guid? ResolvedByActorId { get; private set; }

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public long AggregateRevision { get; private set; }

    public bool BlocksPromotion => State == ListingDisputeState.Open;

    public static ListingDispute Open(
        Guid id,
        Guid listingId,
        string reason,
        Guid actorId,
        DateTimeOffset openedAtUtc)
    {
        RequireIdentifier(id, nameof(id));
        RequireIdentifier(listingId, nameof(listingId));
        RequireIdentifier(actorId, nameof(actorId));
        CatalogClock.RequireUtc(openedAtUtc, nameof(openedAtUtc));
        return new ListingDispute(
            id,
            listingId,
            ListingDisputeState.Open,
            NormalizeReason(reason, nameof(reason)),
            actorId,
            openedAtUtc,
            resolutionReason: null,
            resolvedByActorId: null,
            resolvedAtUtc: null,
            aggregateRevision: 1);
    }

    public static ListingDispute Restore(ListingDisputeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireIdentifier(snapshot.Id, nameof(snapshot));
        RequireIdentifier(snapshot.ListingId, nameof(snapshot));
        RequireIdentifier(snapshot.OpenedByActorId, nameof(snapshot));
        CatalogClock.RequireUtc(snapshot.OpenedAtUtc, nameof(snapshot));
        if (!Enum.IsDefined(snapshot.State) || snapshot.AggregateRevision <= 0)
        {
            throw new CatalogInvariantException(
                "Persisted listing dispute state or aggregate revision is invalid.");
        }

        var openReason = NormalizeReason(snapshot.OpenReason, nameof(snapshot));
        return snapshot.State switch
        {
            ListingDisputeState.Open
                when snapshot.AggregateRevision == 1 &&
                     snapshot.ResolutionReason is null &&
                     snapshot.ResolvedByActorId is null &&
                     snapshot.ResolvedAtUtc is null =>
                new ListingDispute(
                    snapshot.Id,
                    snapshot.ListingId,
                    snapshot.State,
                    openReason,
                    snapshot.OpenedByActorId,
                    snapshot.OpenedAtUtc,
                    null,
                    null,
                    null,
                    snapshot.AggregateRevision),
            ListingDisputeState.Resolved
                when snapshot.AggregateRevision >= 2 &&
                     snapshot.ResolvedByActorId is { } resolverId &&
                     resolverId != Guid.Empty &&
                     snapshot.ResolvedAtUtc is { } resolvedAtUtc &&
                     resolvedAtUtc.Offset == TimeSpan.Zero &&
                     resolvedAtUtc >= snapshot.OpenedAtUtc =>
                new ListingDispute(
                    snapshot.Id,
                    snapshot.ListingId,
                    snapshot.State,
                    openReason,
                    snapshot.OpenedByActorId,
                    snapshot.OpenedAtUtc,
                    NormalizeReason(
                        snapshot.ResolutionReason
                        ?? throw new CatalogInvariantException(
                            "Resolved listing dispute lacks a resolution reason."),
                        nameof(snapshot)),
                    resolverId,
                    resolvedAtUtc,
                    snapshot.AggregateRevision),
            _ => throw new CatalogInvariantException(
                "Persisted listing dispute lifecycle fields are inconsistent."),
        };
    }

    public void Resolve(
        long expectedAggregateRevision,
        Guid actorId,
        string resolutionReason,
        DateTimeOffset resolvedAtUtc)
    {
        if (expectedAggregateRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedAggregateRevision),
                expectedAggregateRevision,
                "Expected dispute revision must be greater than zero.");
        }

        if (AggregateRevision != expectedAggregateRevision)
        {
            throw new CatalogListingDisputeConcurrencyException(
                Id,
                expectedAggregateRevision,
                AggregateRevision);
        }

        RequireIdentifier(actorId, nameof(actorId));
        CatalogClock.RequireUtc(resolvedAtUtc, nameof(resolvedAtUtc));
        if (State != ListingDisputeState.Open)
        {
            throw new CatalogInvariantException(
                $"Listing dispute '{Id}' is already in state '{State}'.");
        }

        if (resolvedAtUtc < OpenedAtUtc)
        {
            throw new CatalogInvariantException(
                "Listing dispute resolution cannot precede its opening timestamp.");
        }

        State = ListingDisputeState.Resolved;
        ResolutionReason = NormalizeReason(
            resolutionReason,
            nameof(resolutionReason));
        ResolvedByActorId = actorId;
        ResolvedAtUtc = resolvedAtUtc;
        AggregateRevision = checked(AggregateRevision + 1);
    }

    public ListingDisputeSnapshot ToSnapshot() =>
        new(
            Id,
            ListingId,
            State,
            OpenReason,
            OpenedByActorId,
            OpenedAtUtc,
            ResolutionReason,
            ResolvedByActorId,
            ResolvedAtUtc,
            AggregateRevision);

    private static void RequireIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty identifier is required.",
                parameterName);
        }
    }

    private static string NormalizeReason(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim().Normalize();
        if (normalized.Length > 2_000 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Listing dispute reason must contain at most 2000 non-control characters.",
                parameterName);
        }

        return normalized;
    }
}

public sealed record ListingDisputeSnapshot(
    Guid Id,
    Guid ListingId,
    ListingDisputeState State,
    string OpenReason,
    Guid OpenedByActorId,
    DateTimeOffset OpenedAtUtc,
    string? ResolutionReason,
    Guid? ResolvedByActorId,
    DateTimeOffset? ResolvedAtUtc,
    long AggregateRevision);

public sealed class CatalogListingDisputeConcurrencyException : InvalidOperationException
{
    public CatalogListingDisputeConcurrencyException(
        Guid disputeId,
        long expectedRevision,
        long actualRevision)
        : base(
            $"Listing dispute '{disputeId}' expected revision '{expectedRevision}' " +
            $"but is at revision '{actualRevision}'.")
    {
        if (disputeId == Guid.Empty)
        {
            throw new ArgumentException("Dispute ID is required.", nameof(disputeId));
        }

        DisputeId = disputeId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public Guid DisputeId { get; }

    public long ExpectedRevision { get; }

    public long ActualRevision { get; }
}
