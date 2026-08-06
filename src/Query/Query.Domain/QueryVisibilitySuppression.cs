namespace Aggregator.Query.Domain;

public enum QueryVisibilitySuppressionTargetKind
{
    Listing = 1,
    Media = 2,
    Contact = 3,
    Route = 4,
    ExternalReference = 5,
}

public enum QueryVisibilitySuppressionResponseMode
{
    HideAsNotFound = 1,
    Gone = 2,
    TemporarilyUnavailable = 3,
    OmitChildElement = 4,
}

public enum QueryVisibilitySuppressionState
{
    Active = 1,
    Resolved = 2,
}

/// <summary>
/// Query-owned projection of the minimal public suppression state emitted by Catalog.
/// It never contains Catalog's private evidence.
/// </summary>
public sealed record QueryVisibilitySuppression
{
    private QueryVisibilitySuppression(
        Guid suppressionId,
        string catalogKey,
        QueryVisibilitySuppressionTargetKind targetKind,
        Guid? listingId,
        string targetKey,
        string publicReasonClass,
        QueryVisibilitySuppressionResponseMode responseMode,
        QueryVisibilitySuppressionState state,
        DateTimeOffset startsAtUtc,
        DateTimeOffset? expiresAtUtc,
        long aggregateRevision,
        DateTimeOffset occurredAtUtc)
    {
        SuppressionId = suppressionId;
        CatalogKey = catalogKey;
        TargetKind = targetKind;
        ListingId = listingId;
        TargetKey = targetKey;
        PublicReasonClass = publicReasonClass;
        ResponseMode = responseMode;
        State = state;
        StartsAtUtc = startsAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        AggregateRevision = aggregateRevision;
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid SuppressionId { get; }

    public string CatalogKey { get; }

    public QueryVisibilitySuppressionTargetKind TargetKind { get; }

    public Guid? ListingId { get; }

    public string TargetKey { get; }

    public string PublicReasonClass { get; }

    public QueryVisibilitySuppressionResponseMode ResponseMode { get; }

    public QueryVisibilitySuppressionState State { get; }

    public DateTimeOffset StartsAtUtc { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public long AggregateRevision { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public bool IsMaterialized => State == QueryVisibilitySuppressionState.Active;

    public bool IsEffectiveAt(DateTimeOffset readAtUtc)
    {
        QueryContractRules.RequireUtc(readAtUtc, nameof(readAtUtc));
        return IsMaterialized &&
               StartsAtUtc <= readAtUtc &&
               (ExpiresAtUtc is null || readAtUtc < ExpiresAtUtc.Value);
    }

    /// <summary>Validates the first public revision emitted by the Catalog suppression lifecycle.</summary>
    public void EnsureValidInitialProjection()
    {
        if (State != QueryVisibilitySuppressionState.Active || AggregateRevision != 2)
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_INITIAL_REVISION_INVALID",
                $"Catalog suppression '{SuppressionId}' must enter Query as active revision '2', but received state '{State}' revision '{AggregateRevision}'.");
        }
    }

    /// <summary>Rejects a producer revision that changes the immutable identity of one suppression.</summary>
    public void EnsureSameIdentity(QueryVisibilitySuppression candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (SuppressionId != candidate.SuppressionId ||
            !string.Equals(CatalogKey, candidate.CatalogKey, StringComparison.Ordinal) ||
            TargetKind != candidate.TargetKind ||
            ListingId != candidate.ListingId ||
            !string.Equals(TargetKey, candidate.TargetKey, StringComparison.Ordinal) ||
            !string.Equals(PublicReasonClass, candidate.PublicReasonClass, StringComparison.Ordinal) ||
            ResponseMode != candidate.ResponseMode ||
            StartsAtUtc != candidate.StartsAtUtc ||
            ExpiresAtUtc != candidate.ExpiresAtUtc)
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_IDENTITY_CHANGED",
                $"Catalog suppression '{SuppressionId}' changed immutable public identity across revisions.");
        }
    }

    /// <summary>Validates the only current public transition: active revision to its exact resolved successor.</summary>
    public void EnsureCanAdvanceTo(QueryVisibilitySuppression candidate)
    {
        EnsureSameIdentity(candidate);
        var expectedRevision = checked(AggregateRevision + 1);
        if (candidate.AggregateRevision != expectedRevision)
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_REVISION_GAP",
                $"Catalog suppression '{SuppressionId}' expected revision '{expectedRevision}' but received '{candidate.AggregateRevision}'.");
        }

        if (State != QueryVisibilitySuppressionState.Active ||
            candidate.State != QueryVisibilitySuppressionState.Resolved)
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_TRANSITION_INVALID",
                $"Catalog suppression '{SuppressionId}' cannot transition from '{State}' to '{candidate.State}'.");
        }

        if (candidate.OccurredAtUtc < OccurredAtUtc)
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_EVENT_TIME_REGRESSION",
                $"Catalog suppression '{SuppressionId}' revision time regressed.");
        }
    }

    public static QueryVisibilitySuppression Create(
        Guid suppressionId,
        string catalogKey,
        QueryVisibilitySuppressionTargetKind targetKind,
        Guid? listingId,
        string targetKey,
        string publicReasonClass,
        QueryVisibilitySuppressionResponseMode responseMode,
        QueryVisibilitySuppressionState state,
        DateTimeOffset startsAtUtc,
        DateTimeOffset? expiresAtUtc,
        long aggregateRevision,
        DateTimeOffset occurredAtUtc)
    {
        QueryContractRules.RequireId(suppressionId, nameof(suppressionId));
        if (!Enum.IsDefined(targetKind))
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_TARGET_KIND_INVALID",
                $"Visibility suppression target kind '{targetKind}' is unsupported.");
        }

        if (!Enum.IsDefined(responseMode))
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_RESPONSE_MODE_INVALID",
                $"Visibility suppression response mode '{responseMode}' is unsupported.");
        }

        if (!Enum.IsDefined(state))
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_STATE_INVALID",
                $"Visibility suppression state '{state}' is unsupported.");
        }

        if (aggregateRevision <= 0)
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_REVISION_INVALID",
                "Visibility suppression aggregate revision must be positive.");
        }

        var normalizedTarget = QueryContractRules.RequireText(targetKey, nameof(targetKey), 500);
        if (targetKind == QueryVisibilitySuppressionTargetKind.Listing)
        {
            if (listingId is not { } exactListingId || exactListingId == Guid.Empty ||
                !Guid.TryParse(normalizedTarget, out var targetListingId) ||
                targetListingId != exactListingId)
            {
                throw new QueryDomainException(
                    "QUERY_VISIBILITY_LISTING_TARGET_INVALID",
                    "A listing suppression requires an exact listing ID equal to its target key.");
            }

            normalizedTarget = exactListingId.ToString("D");
        }
        else
        {
            if (listingId is not null)
            {
                throw new QueryDomainException(
                    "QUERY_VISIBILITY_NON_LISTING_SCOPE_INVALID",
                    "Non-listing suppressions address one exact global target and cannot carry a listing scope.");
            }

            switch (targetKind)
            {
                case QueryVisibilitySuppressionTargetKind.Route:
                    if (normalizedTarget[0] != '/' ||
                        normalizedTarget.Contains("..", StringComparison.Ordinal) ||
                        normalizedTarget.Contains('?') ||
                        normalizedTarget.Contains('#'))
                    {
                        throw new QueryDomainException(
                            "QUERY_VISIBILITY_ROUTE_TARGET_INVALID",
                            "A route suppression target must be an absolute normalized path without query, fragment, or traversal segments.");
                    }

                    break;
                case QueryVisibilitySuppressionTargetKind.Media:
                case QueryVisibilitySuppressionTargetKind.Contact:
                case QueryVisibilitySuppressionTargetKind.ExternalReference:
                    if (!Guid.TryParse(normalizedTarget, out var targetId) || targetId == Guid.Empty)
                    {
                        throw new QueryDomainException(
                            "QUERY_VISIBILITY_CHILD_TARGET_INVALID",
                            "Media, contact, and external-reference suppression targets require a non-empty UUID target key.");
                    }

                    normalizedTarget = targetId.ToString("D");
                    break;
                default:
                    throw new QueryDomainException(
                        "QUERY_VISIBILITY_TARGET_KIND_INVALID",
                        $"Visibility suppression target kind '{targetKind}' is unsupported.");
            }
        }

        var childTarget = targetKind is
            QueryVisibilitySuppressionTargetKind.Media or
            QueryVisibilitySuppressionTargetKind.Contact or
            QueryVisibilitySuppressionTargetKind.ExternalReference;
        if (childTarget != (responseMode == QueryVisibilitySuppressionResponseMode.OmitChildElement))
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_RESPONSE_MODE_MISMATCH",
                childTarget
                    ? "Child suppression targets must omit only the exact child element."
                    : "Listing and route suppressions require an explicit listing response mode.");
        }

        var startsAt = QueryContractRules.RequireUtc(startsAtUtc, nameof(startsAtUtc));
        var occurredAt = QueryContractRules.RequireUtc(occurredAtUtc, nameof(occurredAtUtc));
        if (occurredAt < startsAt)
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_EVENT_PRECEDES_START",
                "Visibility suppression event time cannot precede its start time.");
        }

        if (expiresAtUtc is { } expiry)
        {
            QueryContractRules.RequireUtc(expiry, nameof(expiresAtUtc));
            if (expiry <= startsAt)
            {
                throw new QueryDomainException(
                    "QUERY_VISIBILITY_EXPIRY_INVALID",
                    "Visibility suppression expiry must be later than its start time.");
            }
        }

        return new QueryVisibilitySuppression(
            suppressionId,
            QueryContractRules.RequireKey(catalogKey, nameof(catalogKey)),
            targetKind,
            listingId,
            normalizedTarget,
            QueryContractRules.RequireKey(publicReasonClass, nameof(publicReasonClass)),
            responseMode,
            state,
            startsAt,
            expiresAtUtc,
            aggregateRevision,
            occurredAt);
    }
}
