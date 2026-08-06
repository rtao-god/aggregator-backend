namespace Aggregator.Catalog.Domain;

public enum PublicVisibilitySuppressionTargetKind
{
    Listing = 1,
    Media = 2,
    Contact = 3,
    Route = 4,
    ExternalReference = 5,
}

public enum PublicVisibilitySuppressionResponseMode
{
    HideAsNotFound = 1,
    Gone = 2,
    TemporarilyUnavailable = 3,
    OmitChildElement = 4,
}

public enum PublicVisibilitySuppressionState
{
    Requested = 1,
    Active = 2,
    Resolved = 3,
}

public sealed record PublicVisibilitySuppressionTarget
{
    private PublicVisibilitySuppressionTarget(
        PublicVisibilitySuppressionTargetKind kind,
        Guid? listingId,
        string targetKey)
    {
        Kind = kind;
        ListingId = listingId;
        TargetKey = targetKey;
    }

    public PublicVisibilitySuppressionTargetKind Kind { get; }

    public Guid? ListingId { get; }

    public string TargetKey { get; }

    public static PublicVisibilitySuppressionTarget Create(
        PublicVisibilitySuppressionTargetKind kind,
        Guid? listingId,
        string targetKey)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Suppression target kind is unsupported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        var normalizedTarget = targetKey.Trim();
        if (normalizedTarget.Length > 500)
        {
            throw new ArgumentException("Suppression target key cannot exceed 500 characters.", nameof(targetKey));
        }

        if (kind == PublicVisibilitySuppressionTargetKind.Listing)
        {
            if (listingId is not { } exactListingId || exactListingId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A listing suppression requires its exact listing ID.",
                    nameof(listingId));
            }

            if (!Guid.TryParse(normalizedTarget, out var targetListingId) || targetListingId != exactListingId)
            {
                throw new ArgumentException(
                    "A listing suppression target key must equal the exact listing ID.",
                    nameof(targetKey));
            }

            normalizedTarget = exactListingId.ToString("D");
        }
        else
        {
            if (listingId is not null)
            {
                throw new ArgumentException(
                    "Non-listing suppressions address one exact global target and cannot carry a listing scope.",
                    nameof(listingId));
            }

            if (kind == PublicVisibilitySuppressionTargetKind.Route)
            {
                if (!normalizedTarget.StartsWith("/", StringComparison.Ordinal) ||
                    normalizedTarget.Contains("..", StringComparison.Ordinal) ||
                    normalizedTarget.Contains('?') ||
                    normalizedTarget.Contains('#'))
                {
                    throw new ArgumentException(
                        "A route suppression target must be an absolute normalized path without query, fragment, or traversal segments.",
                        nameof(targetKey));
                }
            }
            else
            {
                if (!Guid.TryParse(normalizedTarget, out var targetId) || targetId == Guid.Empty)
                {
                    throw new ArgumentException(
                        "Media, contact, and external-reference suppression targets require a non-empty UUID target key.",
                        nameof(targetKey));
                }

                normalizedTarget = targetId.ToString("D");
            }
        }

        return new PublicVisibilitySuppressionTarget(kind, listingId, normalizedTarget);
    }
}

public sealed class PublicVisibilitySuppression
{
    private PublicVisibilitySuppression(
        Guid id,
        CatalogKey catalogKey,
        PublicVisibilitySuppressionTarget target,
        string publicReasonClass,
        string privateEvidenceReference,
        PublicVisibilitySuppressionResponseMode responseMode,
        DateTimeOffset startsAtUtc,
        DateTimeOffset? expiresAtUtc,
        PublicVisibilitySuppressionState state,
        long revision,
        Guid changedByActorId,
        string transitionReason,
        DateTimeOffset changedAtUtc)
    {
        Id = id;
        CatalogKey = catalogKey;
        Target = target;
        PublicReasonClass = publicReasonClass;
        PrivateEvidenceReference = privateEvidenceReference;
        ResponseMode = responseMode;
        StartsAtUtc = startsAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        State = state;
        Revision = revision;
        ChangedByActorId = changedByActorId;
        TransitionReason = transitionReason;
        ChangedAtUtc = changedAtUtc;
    }

    public Guid Id { get; }

    public CatalogKey CatalogKey { get; }

    public PublicVisibilitySuppressionTarget Target { get; }

    public string PublicReasonClass { get; }

    public string PrivateEvidenceReference { get; }

    public PublicVisibilitySuppressionResponseMode ResponseMode { get; }

    public DateTimeOffset StartsAtUtc { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public PublicVisibilitySuppressionState State { get; }

    public long Revision { get; }

    public Guid ChangedByActorId { get; }

    public string TransitionReason { get; }

    public DateTimeOffset ChangedAtUtc { get; }

    public static PublicVisibilitySuppression Request(
        Guid id,
        CatalogKey catalogKey,
        PublicVisibilitySuppressionTarget target,
        string publicReasonClass,
        string privateEvidenceReference,
        PublicVisibilitySuppressionResponseMode responseMode,
        DateTimeOffset startsAtUtc,
        DateTimeOffset? expiresAtUtc,
        Guid requestedByActorId,
        string requestReason,
        DateTimeOffset requestedAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Suppression ID is required.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(catalogKey);
        ArgumentNullException.ThrowIfNull(target);
        ValidateResponseMode(target.Kind, responseMode);
        CatalogClock.RequireUtc(startsAtUtc, nameof(startsAtUtc));
        CatalogClock.RequireUtc(requestedAtUtc, nameof(requestedAtUtc));
        if (startsAtUtc != requestedAtUtc)
        {
            throw new CatalogInvariantException(
                "A public visibility suppression starts when its owner command is accepted; deferred activation requires a separate owner contract.");
        }

        if (expiresAtUtc is { } expiry)
        {
            CatalogClock.RequireUtc(expiry, nameof(expiresAtUtc));
            if (expiry <= startsAtUtc)
            {
                throw new ArgumentException("Suppression expiry must be later than its start.", nameof(expiresAtUtc));
            }
        }

        return new PublicVisibilitySuppression(
            id,
            catalogKey,
            target,
            CatalogIdentifier.RequireKey(publicReasonClass, nameof(publicReasonClass)),
            RequireText(privateEvidenceReference, nameof(privateEvidenceReference), 2048),
            responseMode,
            startsAtUtc,
            expiresAtUtc,
            PublicVisibilitySuppressionState.Requested,
            revision: 1,
            RequireActor(requestedByActorId, nameof(requestedByActorId)),
            RequireText(requestReason, nameof(requestReason), 4096),
            requestedAtUtc);
    }

    public PublicVisibilitySuppression Activate(
        long expectedRevision,
        Guid activatedByActorId,
        string activationReason,
        DateTimeOffset activatedAtUtc)
    {
        EnsureRevision(expectedRevision);
        if (State != PublicVisibilitySuppressionState.Requested)
        {
            throw new CatalogInvariantException(
                $"Suppression '{Id}' cannot activate from state '{State}'.");
        }

        CatalogClock.RequireUtc(activatedAtUtc, nameof(activatedAtUtc));
        if (activatedAtUtc < ChangedAtUtc)
        {
            throw new CatalogInvariantException("Suppression activation cannot precede its request.");
        }

        return Transition(
            PublicVisibilitySuppressionState.Active,
            activatedByActorId,
            activationReason,
            activatedAtUtc);
    }

    public PublicVisibilitySuppression Resolve(
        long expectedRevision,
        Guid resolvedByActorId,
        string resolutionReason,
        DateTimeOffset resolvedAtUtc)
    {
        EnsureRevision(expectedRevision);
        if (State != PublicVisibilitySuppressionState.Active)
        {
            throw new CatalogInvariantException(
                $"Suppression '{Id}' cannot resolve from state '{State}'.");
        }

        CatalogClock.RequireUtc(resolvedAtUtc, nameof(resolvedAtUtc));
        if (resolvedAtUtc < ChangedAtUtc)
        {
            throw new CatalogInvariantException("Suppression resolution cannot precede its active revision.");
        }

        return Transition(
            PublicVisibilitySuppressionState.Resolved,
            resolvedByActorId,
            resolutionReason,
            resolvedAtUtc);
    }

    public static PublicVisibilitySuppression Restore(
        Guid id,
        CatalogKey catalogKey,
        PublicVisibilitySuppressionTarget target,
        string publicReasonClass,
        string privateEvidenceReference,
        PublicVisibilitySuppressionResponseMode responseMode,
        DateTimeOffset startsAtUtc,
        DateTimeOffset? expiresAtUtc,
        PublicVisibilitySuppressionState state,
        long revision,
        Guid changedByActorId,
        string transitionReason,
        DateTimeOffset changedAtUtc)
    {
        var requested = Request(
            id,
            catalogKey,
            target,
            publicReasonClass,
            privateEvidenceReference,
            responseMode,
            startsAtUtc,
            expiresAtUtc,
            changedByActorId,
            transitionReason,
            startsAtUtc);
        if (state == PublicVisibilitySuppressionState.Requested && revision == 1)
        {
            return requested;
        }

        var exactPersistedRevision = state switch
        {
            PublicVisibilitySuppressionState.Active => 2L,
            PublicVisibilitySuppressionState.Resolved => 3L,
            _ => throw new CatalogInvariantException(
                "Persisted suppression state and revision are inconsistent."),
        };
        if (revision != exactPersistedRevision)
        {
            throw new CatalogInvariantException("Persisted suppression state and revision are inconsistent.");
        }

        ValidateResponseMode(target.Kind, responseMode);
        CatalogClock.RequireUtc(changedAtUtc, nameof(changedAtUtc));
        if (changedAtUtc < startsAtUtc)
        {
            throw new CatalogInvariantException("Persisted suppression change precedes its start.");
        }

        return new PublicVisibilitySuppression(
            id,
            catalogKey,
            target,
            CatalogIdentifier.RequireKey(publicReasonClass, nameof(publicReasonClass)),
            RequireText(privateEvidenceReference, nameof(privateEvidenceReference), 2048),
            responseMode,
            startsAtUtc,
            expiresAtUtc,
            state,
            revision,
            RequireActor(changedByActorId, nameof(changedByActorId)),
            RequireText(transitionReason, nameof(transitionReason), 4096),
            changedAtUtc);
    }

    private PublicVisibilitySuppression Transition(
        PublicVisibilitySuppressionState state,
        Guid actorId,
        string reason,
        DateTimeOffset changedAtUtc) =>
        new(
            Id,
            CatalogKey,
            Target,
            PublicReasonClass,
            PrivateEvidenceReference,
            ResponseMode,
            StartsAtUtc,
            ExpiresAtUtc,
            state,
            checked(Revision + 1),
            RequireActor(actorId, nameof(actorId)),
            RequireText(reason, nameof(reason), 4096),
            changedAtUtc);

    private void EnsureRevision(long expectedRevision)
    {
        if (expectedRevision != Revision)
        {
            throw new CatalogSuppressionConcurrencyException(Id, expectedRevision, Revision);
        }
    }

    private static void ValidateResponseMode(
        PublicVisibilitySuppressionTargetKind targetKind,
        PublicVisibilitySuppressionResponseMode responseMode)
    {
        if (!Enum.IsDefined(responseMode))
        {
            throw new ArgumentOutOfRangeException(nameof(responseMode), responseMode, "Suppression response mode is unsupported.");
        }

        var childTarget = targetKind is
            PublicVisibilitySuppressionTargetKind.Media or
            PublicVisibilitySuppressionTargetKind.Contact or
            PublicVisibilitySuppressionTargetKind.ExternalReference;
        if (childTarget != (responseMode == PublicVisibilitySuppressionResponseMode.OmitChildElement))
        {
            throw new CatalogInvariantException(
                childTarget
                    ? "Media, contact, and external-reference suppression must omit only the targeted child element."
                    : "Listing and route suppression must use an explicit public response mode rather than child omission.");
        }
    }

    private static Guid RequireActor(Guid actorId, string parameterName) =>
        actorId != Guid.Empty
            ? actorId
            : throw new ArgumentException("Suppression actor ID is required.", parameterName);

    private static string RequireText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException(
                $"Value cannot exceed {maximumLength} characters.",
                parameterName);
        }

        return normalized;
    }
}

public sealed class CatalogSuppressionConcurrencyException : InvalidOperationException
{
    public CatalogSuppressionConcurrencyException(
        Guid suppressionId,
        long expectedRevision,
        long actualRevision)
        : base(
            $"Suppression '{suppressionId}' expected revision '{expectedRevision}' " +
            $"but is at '{actualRevision}'.")
    {
        SuppressionId = suppressionId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public Guid SuppressionId { get; }

    public long ExpectedRevision { get; }

    public long ActualRevision { get; }
}
