using System.Collections.ObjectModel;

namespace Aggregator.Catalog.Domain;

public enum ListingLifecycleState
{
    Draft = 1,
    Approved = 2,
    Published = 3,
    Archived = 4,
}

public enum EditorialDecisionKind
{
    Approved = 1,
    Rejected = 2,
}

public sealed record SubjectReference
{
    private SubjectReference(Guid subjectId, Guid subjectRevisionId, SubjectKind kind)
    {
        SubjectId = subjectId;
        SubjectRevisionId = subjectRevisionId;
        Kind = kind;
    }

    public Guid SubjectId { get; }

    public Guid SubjectRevisionId { get; }

    public SubjectKind Kind { get; }

    public static SubjectReference Create(Guid subjectId, Guid subjectRevisionId, SubjectKind kind)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject ID is required.", nameof(subjectId));
        }

        if (subjectRevisionId == Guid.Empty)
        {
            throw new ArgumentException("Subject revision ID is required.", nameof(subjectRevisionId));
        }

        return new SubjectReference(subjectId, subjectRevisionId, kind);
    }
}

public sealed record ListingRevision
{
    private ListingRevision(
        Guid id,
        Guid listingId,
        long revisionNumber,
        Guid configurationRevisionId,
        SubjectReference subject,
        ListingRevisionContent content,
        string contentDigest,
        Guid createdByActorId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        ListingId = listingId;
        RevisionNumber = revisionNumber;
        ConfigurationRevisionId = configurationRevisionId;
        Subject = subject;
        Content = content;
        ContentDigest = contentDigest;
        CreatedByActorId = createdByActorId;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public Guid ListingId { get; }

    public long RevisionNumber { get; }

    public Guid ConfigurationRevisionId { get; }

    public SubjectReference Subject { get; }

    public ListingRevisionContent Content { get; }

    public string ContentDigest { get; }

    public Guid CreatedByActorId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    internal static ListingRevision Create(
        Guid id,
        Guid listingId,
        long revisionNumber,
        Guid configurationRevisionId,
        SubjectReference subject,
        ListingRevisionContent content,
        string contentDigest,
        Guid createdByActorId,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Listing revision ID is required.", nameof(id));
        }

        if (listingId == Guid.Empty)
        {
            throw new ArgumentException("Listing ID is required.", nameof(listingId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(revisionNumber, 1);
        if (configurationRevisionId == Guid.Empty)
        {
            throw new ArgumentException("Configuration revision ID is required.", nameof(configurationRevisionId));
        }

        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(content);
        if (createdByActorId == Guid.Empty)
        {
            throw new ArgumentException("Actor ID is required.", nameof(createdByActorId));
        }

        CatalogClock.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        return new ListingRevision(
            id,
            listingId,
            revisionNumber,
            configurationRevisionId,
            subject,
            content,
            CatalogDigest.RequireSha256(contentDigest, nameof(contentDigest)),
            createdByActorId,
            createdAtUtc);
    }
}

public sealed record EditorialDecision
{
    private EditorialDecision(
        Guid id,
        Guid listingId,
        Guid revisionId,
        EditorialDecisionKind kind,
        Guid actorId,
        string? reason,
        DateTimeOffset decidedAtUtc)
    {
        Id = id;
        ListingId = listingId;
        RevisionId = revisionId;
        Kind = kind;
        ActorId = actorId;
        Reason = reason;
        DecidedAtUtc = decidedAtUtc;
    }

    public Guid Id { get; }

    public Guid ListingId { get; }

    public Guid RevisionId { get; }

    public EditorialDecisionKind Kind { get; }

    public Guid ActorId { get; }

    public string? Reason { get; }

    public DateTimeOffset DecidedAtUtc { get; }

    internal static EditorialDecision Create(
        Guid id,
        Guid listingId,
        Guid revisionId,
        EditorialDecisionKind kind,
        Guid actorId,
        string? reason,
        DateTimeOffset decidedAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Decision ID is required.", nameof(id));
        }

        if (listingId == Guid.Empty)
        {
            throw new ArgumentException("Listing ID is required.", nameof(listingId));
        }

        if (revisionId == Guid.Empty)
        {
            throw new ArgumentException("Revision ID is required.", nameof(revisionId));
        }

        if (actorId == Guid.Empty)
        {
            throw new ArgumentException("Actor ID is required.", nameof(actorId));
        }

        if (kind == EditorialDecisionKind.Rejected && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection reason is required.", nameof(reason));
        }

        CatalogClock.RequireUtc(decidedAtUtc, nameof(decidedAtUtc));
        return new EditorialDecision(
            id,
            listingId,
            revisionId,
            kind,
            actorId,
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            decidedAtUtc);
    }
}

public sealed class Listing
{
    private Listing(
        Guid id,
        CatalogKey catalogKey,
        SubjectReference subject,
        ListingLifecycleState state,
        long version,
        long latestRevisionNumber,
        Guid? currentDraftRevisionId,
        Guid? approvedRevisionId,
        Guid? publishedRevisionId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        Id = id;
        CatalogKey = catalogKey;
        Subject = subject;
        State = state;
        Version = version;
        LatestRevisionNumber = latestRevisionNumber;
        CurrentDraftRevisionId = currentDraftRevisionId;
        ApprovedRevisionId = approvedRevisionId;
        PublishedRevisionId = publishedRevisionId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid Id { get; }

    public CatalogKey CatalogKey { get; }

    public SubjectReference Subject { get; private set; }

    public ListingLifecycleState State { get; private set; }

    public long Version { get; private set; }

    public long LatestRevisionNumber { get; private set; }

    public Guid? CurrentDraftRevisionId { get; private set; }

    public Guid? ApprovedRevisionId { get; private set; }

    public Guid? PublishedRevisionId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static Listing Create(Guid id, CatalogKey catalogKey, SubjectReference subject, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Listing ID is required.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(catalogKey);
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.Kind == SubjectKind.Organization)
        {
            throw new CatalogInvariantException("An organization cannot be the public subject of a listing.");
        }

        CatalogClock.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        return new Listing(
            id,
            catalogKey,
            subject,
            ListingLifecycleState.Draft,
            version: 1,
            latestRevisionNumber: 0,
            currentDraftRevisionId: null,
            approvedRevisionId: null,
            publishedRevisionId: null,
            createdAtUtc,
            createdAtUtc);
    }

    public static Listing Restore(ListingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Id == Guid.Empty || snapshot.Version < 1 || snapshot.LatestRevisionNumber < 0)
        {
            throw new ArgumentException("Listing snapshot is invalid.", nameof(snapshot));
        }

        return new Listing(
            snapshot.Id,
            CatalogKey.Create(snapshot.CatalogKey),
            SubjectReference.Create(snapshot.SubjectId, snapshot.SubjectRevisionId, snapshot.SubjectKind),
            snapshot.State,
            snapshot.Version,
            snapshot.LatestRevisionNumber,
            snapshot.CurrentDraftRevisionId,
            snapshot.ApprovedRevisionId,
            snapshot.PublishedRevisionId,
            snapshot.CreatedAtUtc,
            snapshot.UpdatedAtUtc);
    }

    public ListingRevision AddDraftRevision(
        Guid revisionId,
        long expectedVersion,
        Guid configurationRevisionId,
        SubjectReference subject,
        ListingRevisionContent content,
        string contentDigest,
        Guid actorId,
        DateTimeOffset createdAtUtc)
    {
        EnsureVersion(expectedVersion);
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(content);
        if (subject.SubjectId != Subject.SubjectId || subject.Kind != Subject.Kind)
        {
            throw new CatalogInvariantException("A listing revision cannot replace the listing subject identity or kind.");
        }

        var revision = ListingRevision.Create(
            revisionId,
            Id,
            checked(LatestRevisionNumber + 1),
            configurationRevisionId,
            subject,
            content,
            contentDigest,
            actorId,
            createdAtUtc);

        Subject = subject;
        LatestRevisionNumber = revision.RevisionNumber;
        CurrentDraftRevisionId = revision.Id;
        ApprovedRevisionId = null;
        State = ListingLifecycleState.Draft;
        AdvanceVersion(createdAtUtc);
        return revision;
    }

    public EditorialDecision Approve(
        Guid decisionId,
        Guid revisionId,
        long expectedVersion,
        ListingRevisionContent content,
        ProductConfiguration configuration,
        Guid actorId,
        DateTimeOffset decidedAtUtc)
    {
        EnsureVersion(expectedVersion);
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(configuration);
        if (CurrentDraftRevisionId != revisionId)
        {
            throw new CatalogInvariantException("Only the current draft revision can be approved.");
        }

        content.EnsurePublishable(configuration);
        ApprovedRevisionId = revisionId;
        State = ListingLifecycleState.Approved;
        AdvanceVersion(decidedAtUtc);
        return EditorialDecision.Create(
            decisionId,
            Id,
            revisionId,
            EditorialDecisionKind.Approved,
            actorId,
            reason: null,
            decidedAtUtc);
    }

    public EditorialDecision Reject(
        Guid decisionId,
        Guid revisionId,
        long expectedVersion,
        Guid actorId,
        string reason,
        DateTimeOffset decidedAtUtc)
    {
        EnsureVersion(expectedVersion);
        EnsureMutable();
        if (CurrentDraftRevisionId != revisionId)
        {
            throw new CatalogInvariantException("Only the current draft revision can be rejected.");
        }

        ApprovedRevisionId = null;
        State = ListingLifecycleState.Draft;
        AdvanceVersion(decidedAtUtc);
        return EditorialDecision.Create(
            decisionId,
            Id,
            revisionId,
            EditorialDecisionKind.Rejected,
            actorId,
            reason,
            decidedAtUtc);
    }

    public void MarkPublished(Guid revisionId, long expectedVersion, DateTimeOffset publishedAtUtc)
    {
        EnsureVersion(expectedVersion);
        EnsureMutable();
        if (ApprovedRevisionId != revisionId)
        {
            throw new CatalogInvariantException("Only the exact approved revision can be published.");
        }

        PublishedRevisionId = revisionId;
        State = ListingLifecycleState.Published;
        AdvanceVersion(publishedAtUtc);
    }

    public void Archive(long expectedVersion, DateTimeOffset archivedAtUtc)
    {
        EnsureVersion(expectedVersion);
        EnsureMutable();
        State = ListingLifecycleState.Archived;
        CurrentDraftRevisionId = null;
        ApprovedRevisionId = null;
        AdvanceVersion(archivedAtUtc);
    }

    public ListingSnapshot ToSnapshot() =>
        new(
            Id,
            CatalogKey.Value,
            Subject.SubjectId,
            Subject.SubjectRevisionId,
            Subject.Kind,
            State,
            Version,
            LatestRevisionNumber,
            CurrentDraftRevisionId,
            ApprovedRevisionId,
            PublishedRevisionId,
            CreatedAtUtc,
            UpdatedAtUtc);

    private void EnsureVersion(long expectedVersion)
    {
        if (expectedVersion != Version)
        {
            throw new CatalogConcurrencyException(Id, expectedVersion, Version);
        }
    }

    private void EnsureMutable()
    {
        if (State == ListingLifecycleState.Archived)
        {
            throw new CatalogInvariantException($"Listing '{Id}' is archived.");
        }
    }

    private void AdvanceVersion(DateTimeOffset changedAtUtc)
    {
        CatalogClock.RequireUtc(changedAtUtc, nameof(changedAtUtc));
        if (changedAtUtc < UpdatedAtUtc)
        {
            throw new CatalogInvariantException("Listing change time cannot move backwards.");
        }

        Version = checked(Version + 1);
        UpdatedAtUtc = changedAtUtc;
    }
}

public sealed record ListingSnapshot(
    Guid Id,
    string CatalogKey,
    Guid SubjectId,
    Guid SubjectRevisionId,
    SubjectKind SubjectKind,
    ListingLifecycleState State,
    long Version,
    long LatestRevisionNumber,
    Guid? CurrentDraftRevisionId,
    Guid? ApprovedRevisionId,
    Guid? PublishedRevisionId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed class CatalogConcurrencyException : InvalidOperationException
{
    public CatalogConcurrencyException(Guid listingId, long expectedVersion, long actualVersion)
        : base($"Listing '{listingId}' expected version '{expectedVersion}' but is at version '{actualVersion}'.")
    {
        ListingId = listingId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public Guid ListingId { get; }

    public long ExpectedVersion { get; }

    public long ActualVersion { get; }
}

public sealed record PublicationEntry
{
    private PublicationEntry(
        Guid listingId,
        Guid listingRevisionId,
        Guid subjectRevisionId,
        string contentDigest)
    {
        ListingId = listingId;
        ListingRevisionId = listingRevisionId;
        SubjectRevisionId = subjectRevisionId;
        ContentDigest = contentDigest;
    }

    public Guid ListingId { get; }

    public Guid ListingRevisionId { get; }

    public Guid SubjectRevisionId { get; }

    public string ContentDigest { get; }

    public static PublicationEntry Create(
        Guid listingId,
        Guid listingRevisionId,
        Guid subjectRevisionId,
        string contentDigest)
    {
        if (listingId == Guid.Empty)
        {
            throw new ArgumentException("Listing ID is required.", nameof(listingId));
        }

        if (listingRevisionId == Guid.Empty)
        {
            throw new ArgumentException("Listing revision ID is required.", nameof(listingRevisionId));
        }

        if (subjectRevisionId == Guid.Empty)
        {
            throw new ArgumentException("Subject revision ID is required.", nameof(subjectRevisionId));
        }

        return new PublicationEntry(
            listingId,
            listingRevisionId,
            subjectRevisionId,
            CatalogDigest.RequireSha256(contentDigest, nameof(contentDigest)));
    }
}

public sealed record CatalogPublication
{
    private CatalogPublication(
        Guid id,
        CatalogKey catalogKey,
        Guid configurationRevisionId,
        long sequence,
        string artifactKey,
        string artifactDigest,
        IReadOnlyList<PublicationEntry> entries,
        Guid createdByActorId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        CatalogKey = catalogKey;
        ConfigurationRevisionId = configurationRevisionId;
        Sequence = sequence;
        ArtifactKey = artifactKey;
        ArtifactDigest = artifactDigest;
        Entries = entries;
        CreatedByActorId = createdByActorId;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public CatalogKey CatalogKey { get; }

    public Guid ConfigurationRevisionId { get; }

    public long Sequence { get; }

    public string ArtifactKey { get; }

    public string ArtifactDigest { get; }

    public IReadOnlyList<PublicationEntry> Entries { get; }

    public Guid CreatedByActorId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static CatalogPublication Create(
        Guid id,
        CatalogKey catalogKey,
        Guid configurationRevisionId,
        long sequence,
        string artifactKey,
        string artifactDigest,
        IEnumerable<PublicationEntry> entries,
        Guid createdByActorId,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Publication ID is required.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(catalogKey);
        if (configurationRevisionId == Guid.Empty)
        {
            throw new ArgumentException("Configuration revision ID is required.", nameof(configurationRevisionId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactKey);
        ArgumentNullException.ThrowIfNull(entries);
        if (createdByActorId == Guid.Empty)
        {
            throw new ArgumentException("Actor ID is required.", nameof(createdByActorId));
        }

        CatalogClock.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        var materializedEntries = entries.OrderBy(entry => entry.ListingId).ToArray();
        if (materializedEntries.Select(entry => entry.ListingId).Distinct().Count() != materializedEntries.Length)
        {
            throw new ArgumentException("A publication cannot contain multiple revisions of the same listing.", nameof(entries));
        }

        return new CatalogPublication(
            id,
            catalogKey,
            configurationRevisionId,
            sequence,
            artifactKey.Trim(),
            CatalogDigest.RequireSha256(artifactDigest, nameof(artifactDigest)),
            Array.AsReadOnly(materializedEntries),
            createdByActorId,
            createdAtUtc);
    }
}

public sealed record CurrentPublicationPointer(
    CatalogKey CatalogKey,
    Guid PublicationId,
    long PublicationSequence,
    DateTimeOffset ActivatedAtUtc,
    Guid ActivatedByActorId)
{
    public static CurrentPublicationPointer Create(
        CatalogKey catalogKey,
        Guid publicationId,
        long publicationSequence,
        DateTimeOffset activatedAtUtc,
        Guid activatedByActorId)
    {
        ArgumentNullException.ThrowIfNull(catalogKey);
        if (publicationId == Guid.Empty)
        {
            throw new ArgumentException("Publication ID is required.", nameof(publicationId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(publicationSequence, 1);
        CatalogClock.RequireUtc(activatedAtUtc, nameof(activatedAtUtc));
        if (activatedByActorId == Guid.Empty)
        {
            throw new ArgumentException("Actor ID is required.", nameof(activatedByActorId));
        }

        return new CurrentPublicationPointer(
            catalogKey,
            publicationId,
            publicationSequence,
            activatedAtUtc,
            activatedByActorId);
    }
}

public sealed record PublicationManifest(
    Guid PublicationId,
    string CatalogKey,
    Guid ConfigurationRevisionId,
    long Sequence,
    IReadOnlyList<PublicationEntry> Entries,
    DateTimeOffset CreatedAtUtc)
{
    public static PublicationManifest Create(CatalogPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return new PublicationManifest(
            publication.Id,
            publication.CatalogKey.Value,
            publication.ConfigurationRevisionId,
            publication.Sequence,
            new ReadOnlyCollection<PublicationEntry>(publication.Entries.ToArray()),
            publication.CreatedAtUtc);
    }
}
