namespace Aggregator.Catalog.Domain;

/// <summary>
/// Owns the Listing-side state transition caused by activating or removing an exact Catalog publication membership.
/// </summary>
public static class ListingPublicationMembership
{
    public static Listing PublishApproved(
        Listing listing,
        Guid revisionId,
        long expectedVersion,
        DateTimeOffset activatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(listing);
        listing.MarkPublished(revisionId, expectedVersion, activatedAtUtc);
        return listing;
    }

    public static Listing RestoreExactPublishedRevision(
        Listing listing,
        Guid revisionId,
        DateTimeOffset activatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(listing);
        if (revisionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Published listing revision ID is required.",
                nameof(revisionId));
        }

        var snapshot = listing.ToSnapshot();
        EnsureActivationAllowed(snapshot, activatedAtUtc);
        if (snapshot.PublishedRevisionId == revisionId)
        {
            return listing;
        }

        var state = snapshot.State == ListingLifecycleState.Approved &&
                    snapshot.ApprovedRevisionId == revisionId
            ? ListingLifecycleState.Published
            : snapshot.State;
        return Listing.Restore(snapshot with
        {
            State = state,
            Version = checked(snapshot.Version + 1),
            PublishedRevisionId = revisionId,
            UpdatedAtUtc = activatedAtUtc,
        });
    }

    public static Listing RemoveFromPublication(
        Listing listing,
        DateTimeOffset activatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var snapshot = listing.ToSnapshot();
        EnsureTimestamp(snapshot, activatedAtUtc);
        if (snapshot.PublishedRevisionId is null)
        {
            return listing;
        }

        var state = snapshot.State switch
        {
            ListingLifecycleState.Published when snapshot.ApprovedRevisionId is not null =>
                ListingLifecycleState.Approved,
            ListingLifecycleState.Published => ListingLifecycleState.Draft,
            _ => snapshot.State,
        };
        return Listing.Restore(snapshot with
        {
            State = state,
            Version = checked(snapshot.Version + 1),
            PublishedRevisionId = null,
            UpdatedAtUtc = activatedAtUtc,
        });
    }

    public static Listing ArchiveAndRemoveFromPublication(
        Listing listing,
        long expectedVersion,
        DateTimeOffset archivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var snapshot = listing.ToSnapshot();
        if (snapshot.Version != expectedVersion)
        {
            throw new CatalogConcurrencyException(
                snapshot.Id,
                expectedVersion,
                snapshot.Version);
        }

        if (snapshot.State == ListingLifecycleState.Archived)
        {
            throw new CatalogInvariantException(
                $"Listing '{snapshot.Id}' is archived.");
        }

        EnsureTimestamp(snapshot, archivedAtUtc);
        return Listing.Restore(snapshot with
        {
            State = ListingLifecycleState.Archived,
            Version = checked(snapshot.Version + 1),
            CurrentDraftRevisionId = null,
            ApprovedRevisionId = null,
            PublishedRevisionId = null,
            UpdatedAtUtc = archivedAtUtc,
        });
    }

    private static void EnsureActivationAllowed(
        ListingSnapshot snapshot,
        DateTimeOffset activatedAtUtc)
    {
        if (snapshot.State == ListingLifecycleState.Archived)
        {
            throw new CatalogInvariantException(
                $"Archived listing '{snapshot.Id}' cannot be restored into a Catalog publication.");
        }

        EnsureTimestamp(snapshot, activatedAtUtc);
    }

    private static void EnsureTimestamp(
        ListingSnapshot snapshot,
        DateTimeOffset changedAtUtc)
    {
        CatalogClock.RequireUtc(changedAtUtc, nameof(changedAtUtc));
        if (changedAtUtc < snapshot.UpdatedAtUtc)
        {
            throw new CatalogInvariantException(
                "Listing publication membership time cannot move backwards.");
        }
    }
}
