namespace Aggregator.Catalog.Domain;

public enum ListingKind
{
    Place = 1,
    Provider = 2,
}

public enum ListingLifecycleState
{
    Created = 1,
    Draft = 2,
    ReviewRequired = 3,
    Approved = 4,
    PublicationRequested = 5,
    Published = 6,
    Stale = 7,
    Archived = 8,
    Rejected = 9,
    Disputed = 10,
    Blocked = 11,
}

public abstract record ListingSubject
{
    private protected ListingSubject()
    {
    }

    public abstract ListingKind Kind { get; }

    public abstract Guid SubjectId { get; }

    public static ListingSubject ForPlace(PlaceId placeId)
    {
        CatalogTextRules.RequireIdentifier(placeId.Value, nameof(placeId));
        return new PlaceListingSubject(placeId);
    }

    public static ListingSubject ForProvider(ProviderId providerId)
    {
        CatalogTextRules.RequireIdentifier(providerId.Value, nameof(providerId));
        return new ProviderListingSubject(providerId);
    }
}

public sealed record PlaceListingSubject : ListingSubject
{
    internal PlaceListingSubject(PlaceId placeId)
    {
        PlaceId = placeId;
    }

    public PlaceId PlaceId { get; }

    public override ListingKind Kind => ListingKind.Place;

    public override Guid SubjectId => PlaceId.Value;
}

public sealed record ProviderListingSubject : ListingSubject
{
    internal ProviderListingSubject(ProviderId providerId)
    {
        ProviderId = providerId;
    }

    public ProviderId ProviderId { get; }

    public override ListingKind Kind => ListingKind.Provider;

    public override Guid SubjectId => ProviderId.Value;
}

public sealed class Listing
{
    private Listing(
        ListingId id,
        CatalogId catalogId,
        ListingSubject subject,
        ListingLifecycleState state,
        ListingRevisionId? currentDraftRevisionId,
        ListingRevisionId? currentPublishedRevisionId,
        PublicationId? currentPublicationId,
        long aggregateRevision,
        string? archiveReason,
        ActorId lastChangedBy,
        DateTimeOffset lastChangedAtUtc)
    {
        Id = id;
        CatalogId = catalogId;
        Subject = subject;
        State = state;
        CurrentDraftRevisionId = currentDraftRevisionId;
        CurrentPublishedRevisionId = currentPublishedRevisionId;
        CurrentPublicationId = currentPublicationId;
        AggregateRevision = aggregateRevision;
        ArchiveReason = archiveReason;
        LastChangedBy = lastChangedBy;
        LastChangedAtUtc = lastChangedAtUtc;
    }

    public ListingId Id { get; }

    public CatalogId CatalogId { get; }

    public ListingSubject Subject { get; }

    public ListingLifecycleState State { get; private set; }

    public ListingRevisionId? CurrentDraftRevisionId { get; private set; }

    public ListingRevisionId? CurrentPublishedRevisionId { get; private set; }

    public PublicationId? CurrentPublicationId { get; private set; }

    public long AggregateRevision { get; private set; }

    public string? ArchiveReason { get; private set; }

    public ActorId LastChangedBy { get; private set; }

    public DateTimeOffset LastChangedAtUtc { get; private set; }

    public static Listing Create(
        ListingId id,
        CatalogId catalogId,
        ListingSubject subject,
        ActorId actorId,
        DateTimeOffset createdAtUtc)
    {
        CatalogTextRules.RequireIdentifier(id.Value, nameof(id));
        CatalogTextRules.RequireIdentifier(catalogId.Value, nameof(catalogId));
        ArgumentNullException.ThrowIfNull(subject);
        CatalogTextRules.RequireIdentifier(subject.SubjectId, nameof(subject));
        CatalogTextRules.RequireIdentifier(actorId.Value, nameof(actorId));
        CatalogTextRules.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        return new Listing(
            id,
            catalogId,
            subject,
            ListingLifecycleState.Created,
            null,
            null,
            null,
            1,
            null,
            actorId,
            createdAtUtc);
    }

    public static Listing Restore(
        ListingId id,
        CatalogId catalogId,
        ListingSubject subject,
        ListingLifecycleState state,
        ListingRevisionId? currentDraftRevisionId,
        ListingRevisionId? currentPublishedRevisionId,
        PublicationId? currentPublicationId,
        long aggregateRevision,
        string? archiveReason,
        ActorId lastChangedBy,
        DateTimeOffset lastChangedAtUtc)
    {
        var listing = Create(id, catalogId, subject, lastChangedBy, lastChangedAtUtc);
        if (aggregateRevision < 1)
        {
            throw new CatalogDomainException("LISTING_REVISION_INVALID", "Aggregate revision must be positive.");
        }

        if (currentDraftRevisionId is { } draft)
        {
            CatalogTextRules.RequireIdentifier(draft.Value, nameof(currentDraftRevisionId));
        }

        if (currentPublishedRevisionId is { } published)
        {
            CatalogTextRules.RequireIdentifier(published.Value, nameof(currentPublishedRevisionId));
        }

        if (currentPublicationId is { } publication)
        {
            CatalogTextRules.RequireIdentifier(publication.Value, nameof(currentPublicationId));
        }

        if ((currentPublishedRevisionId is null) != (currentPublicationId is null))
        {
            throw new CatalogDomainException(
                "LISTING_PUBLIC_POINTER_INCONSISTENT",
                "Published revision and publication pointers must either both exist or both be absent.");
        }

        if (state is ListingLifecycleState.Draft
            or ListingLifecycleState.ReviewRequired
            or ListingLifecycleState.Approved
            or ListingLifecycleState.PublicationRequested
            or ListingLifecycleState.Rejected
            && currentDraftRevisionId is null)
        {
            throw new CatalogDomainException("LISTING_DRAFT_POINTER_REQUIRED", $"State '{state}' requires a current draft revision.");
        }

        listing.State = state;
        listing.CurrentDraftRevisionId = currentDraftRevisionId;
        listing.CurrentPublishedRevisionId = currentPublishedRevisionId;
        listing.CurrentPublicationId = currentPublicationId;
        listing.AggregateRevision = aggregateRevision;
        listing.ArchiveReason = archiveReason;
        return listing;
    }

    public void AttachDraftRevision(
        ListingRevisionId revisionId,
        long expectedAggregateRevision,
        ActorId actorId,
        DateTimeOffset changedAtUtc)
    {
        EnsureMutable(expectedAggregateRevision);
        CatalogTextRules.RequireIdentifier(revisionId.Value, nameof(revisionId));
        EnsureActorAndTime(actorId, changedAtUtc);
        CurrentDraftRevisionId = revisionId;
        State = ListingLifecycleState.Draft;
        Touch(actorId, changedAtUtc);
    }

    public void SubmitForReview(long expectedAggregateRevision, ActorId actorId, DateTimeOffset changedAtUtc)
    {
        EnsureMutable(expectedAggregateRevision);
        EnsureState(ListingLifecycleState.Draft);
        EnsureDraftExists();
        EnsureActorAndTime(actorId, changedAtUtc);
        State = ListingLifecycleState.ReviewRequired;
        Touch(actorId, changedAtUtc);
    }

    public void Approve(long expectedAggregateRevision, ActorId actorId, DateTimeOffset changedAtUtc)
    {
        EnsureMutable(expectedAggregateRevision);
        EnsureState(ListingLifecycleState.ReviewRequired);
        EnsureDraftExists();
        EnsureActorAndTime(actorId, changedAtUtc);
        State = ListingLifecycleState.Approved;
        Touch(actorId, changedAtUtc);
    }

    public void Reject(long expectedAggregateRevision, ActorId actorId, DateTimeOffset changedAtUtc)
    {
        EnsureMutable(expectedAggregateRevision);
        EnsureState(ListingLifecycleState.ReviewRequired);
        EnsureDraftExists();
        EnsureActorAndTime(actorId, changedAtUtc);
        State = ListingLifecycleState.Rejected;
        Touch(actorId, changedAtUtc);
    }

    public void RequestPublication(long expectedAggregateRevision, ActorId actorId, DateTimeOffset changedAtUtc)
    {
        EnsureMutable(expectedAggregateRevision);
        EnsureState(ListingLifecycleState.Approved);
        EnsureDraftExists();
        EnsureActorAndTime(actorId, changedAtUtc);
        State = ListingLifecycleState.PublicationRequested;
        Touch(actorId, changedAtUtc);
    }

    public void MarkPublished(
        ListingRevisionId revisionId,
        PublicationId publicationId,
        long expectedAggregateRevision,
        ActorId actorId,
        DateTimeOffset changedAtUtc)
    {
        EnsureMutable(expectedAggregateRevision);
        EnsureState(ListingLifecycleState.PublicationRequested);
        EnsureDraftExists();
        if (CurrentDraftRevisionId != revisionId)
        {
            throw new CatalogDomainException(
                "LISTING_PUBLICATION_REVISION_MISMATCH",
                "Publication must activate the exact approved draft revision.");
        }

        CatalogTextRules.RequireIdentifier(publicationId.Value, nameof(publicationId));
        EnsureActorAndTime(actorId, changedAtUtc);
        CurrentPublishedRevisionId = revisionId;
        CurrentPublicationId = publicationId;
        State = ListingLifecycleState.Published;
        Touch(actorId, changedAtUtc);
    }

    public void MarkStale(long expectedAggregateRevision, ActorId actorId, DateTimeOffset changedAtUtc)
    {
        EnsureMutable(expectedAggregateRevision);
        EnsureState(ListingLifecycleState.Published);
        EnsureActorAndTime(actorId, changedAtUtc);
        State = ListingLifecycleState.Stale;
        Touch(actorId, changedAtUtc);
    }

    public void Dispute(long expectedAggregateRevision, ActorId actorId, DateTimeOffset changedAtUtc)
    {
        EnsureMutable(expectedAggregateRevision);
        EnsureActorAndTime(actorId, changedAtUtc);
        State = ListingLifecycleState.Disputed;
        Touch(actorId, changedAtUtc);
    }

    public void Archive(
        string reason,
        long expectedAggregateRevision,
        ActorId actorId,
        DateTimeOffset changedAtUtc)
    {
        EnsureMutable(expectedAggregateRevision);
        CatalogTextRules.RequireKey(reason, nameof(reason));
        EnsureActorAndTime(actorId, changedAtUtc);
        ArchiveReason = reason;
        State = ListingLifecycleState.Archived;
        Touch(actorId, changedAtUtc);
    }

    private void EnsureMutable(long expectedAggregateRevision)
    {
        if (State == ListingLifecycleState.Archived)
        {
            throw new CatalogDomainException("LISTING_ARCHIVED", "Archived listings cannot be mutated by the ordinary lifecycle.");
        }

        if (expectedAggregateRevision != AggregateRevision)
        {
            throw new CatalogDomainException(
                "LISTING_REVISION_CONFLICT",
                $"Expected aggregate revision {expectedAggregateRevision}, actual revision {AggregateRevision}.");
        }
    }

    private void EnsureState(ListingLifecycleState expected)
    {
        if (State != expected)
        {
            throw new CatalogDomainException(
                "LISTING_STATE_TRANSITION_INVALID",
                $"Listing state '{State}' cannot execute a transition that requires '{expected}'.");
        }
    }

    private void EnsureDraftExists()
    {
        if (CurrentDraftRevisionId is null)
        {
            throw new CatalogDomainException("LISTING_DRAFT_POINTER_REQUIRED", "A current draft revision is required.");
        }
    }

    private static void EnsureActorAndTime(ActorId actorId, DateTimeOffset changedAtUtc)
    {
        CatalogTextRules.RequireIdentifier(actorId.Value, nameof(actorId));
        CatalogTextRules.RequireUtc(changedAtUtc, nameof(changedAtUtc));
    }

    private void Touch(ActorId actorId, DateTimeOffset changedAtUtc)
    {
        AggregateRevision++;
        LastChangedBy = actorId;
        LastChangedAtUtc = changedAtUtc;
    }
}
