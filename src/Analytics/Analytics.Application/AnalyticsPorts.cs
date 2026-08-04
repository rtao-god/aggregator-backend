using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

public enum PublicReadMembershipState
{
    Known = 1,
    UnknownRevision = 2,
    CatalogMismatch = 3,
    ListingNotPublic = 4,
    ListingRequired = 5,
}

public sealed record PublicReadMembershipResult(
    PublicReadMembershipState State,
    string? ActualCatalogKey,
    Guid? ActualListingId);

/// <summary>One immutable Query activation projected into Analytics for local event validation.</summary>
public sealed record PublicReadReferenceProjection
{
    private PublicReadReferenceProjection(
        Guid publicReadRevisionId,
        string catalogKey,
        Guid baseProjectionId,
        Guid promotionOverlayId,
        Guid safetyOverlayId,
        Guid sourcePublicationId,
        string publicReadContentDigest,
        string membershipDigest,
        DateTimeOffset activatedAtUtc,
        IReadOnlyList<Guid> publicListingIds)
    {
        PublicReadRevisionId = publicReadRevisionId;
        CatalogKey = catalogKey;
        BaseProjectionId = baseProjectionId;
        PromotionOverlayId = promotionOverlayId;
        SafetyOverlayId = safetyOverlayId;
        SourcePublicationId = sourcePublicationId;
        PublicReadContentDigest = publicReadContentDigest;
        MembershipDigest = membershipDigest;
        ActivatedAtUtc = activatedAtUtc;
        PublicListingIds = publicListingIds;
    }

    public Guid PublicReadRevisionId { get; }

    public string CatalogKey { get; }

    public Guid BaseProjectionId { get; }

    public Guid PromotionOverlayId { get; }

    public Guid SafetyOverlayId { get; }

    public Guid SourcePublicationId { get; }

    public string PublicReadContentDigest { get; }

    public string MembershipDigest { get; }

    public DateTimeOffset ActivatedAtUtc { get; }

    public IReadOnlyList<Guid> PublicListingIds { get; }

    public static PublicReadReferenceProjection Create(
        Guid publicReadRevisionId,
        string catalogKey,
        Guid baseProjectionId,
        Guid promotionOverlayId,
        Guid safetyOverlayId,
        Guid sourcePublicationId,
        string publicReadContentDigest,
        string membershipDigest,
        DateTimeOffset activatedAtUtc,
        IEnumerable<Guid> publicListingIds)
    {
        AnalyticsDomainRules.RequireIdentifier(publicReadRevisionId, nameof(publicReadRevisionId));
        var normalizedCatalogKey = AnalyticsDomainRules.RequireKey(catalogKey, nameof(catalogKey));
        AnalyticsDomainRules.RequireIdentifier(baseProjectionId, nameof(baseProjectionId));
        AnalyticsDomainRules.RequireIdentifier(promotionOverlayId, nameof(promotionOverlayId));
        AnalyticsDomainRules.RequireIdentifier(safetyOverlayId, nameof(safetyOverlayId));
        AnalyticsDomainRules.RequireIdentifier(sourcePublicationId, nameof(sourcePublicationId));
        var normalizedContentDigest = AnalyticsDomainRules.RequireDigest(
            publicReadContentDigest,
            nameof(publicReadContentDigest));
        var normalizedMembershipDigest = AnalyticsDomainRules.RequireDigest(
            membershipDigest,
            nameof(membershipDigest));
        AnalyticsDomainRules.RequireUtc(activatedAtUtc, nameof(activatedAtUtc));
        ArgumentNullException.ThrowIfNull(publicListingIds);
        var listingIds = publicListingIds.Order().ToArray();
        if (listingIds.Any(listingId => listingId == Guid.Empty))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_LISTING_ID_INVALID",
                "Public-read membership cannot contain an empty listing ID.");
        }

        if (listingIds.Distinct().Count() != listingIds.Length)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_LISTING_DUPLICATE",
                "Public-read membership cannot contain duplicate listing IDs.");
        }

        return new PublicReadReferenceProjection(
            publicReadRevisionId,
            normalizedCatalogKey,
            baseProjectionId,
            promotionOverlayId,
            safetyOverlayId,
            sourcePublicationId,
            normalizedContentDigest,
            normalizedMembershipDigest,
            activatedAtUtc,
            Array.AsReadOnly(listingIds));
    }
}

/// <summary>One Catalog-owned listing permission projected locally for Analytics report authorization.</summary>
public sealed record ListingMetricsAccessProjection
{
    private ListingMetricsAccessProjection(
        Guid listingId,
        Guid actorId,
        bool canViewAnalytics,
        long sourceAggregateRevision,
        string sourcePayloadDigest,
        DateTimeOffset changedAtUtc)
    {
        ListingId = listingId;
        ActorId = actorId;
        CanViewAnalytics = canViewAnalytics;
        SourceAggregateRevision = sourceAggregateRevision;
        SourcePayloadDigest = sourcePayloadDigest;
        ChangedAtUtc = changedAtUtc;
    }

    public Guid ListingId { get; }

    public Guid ActorId { get; }

    public bool CanViewAnalytics { get; }

    public long SourceAggregateRevision { get; }

    public string SourcePayloadDigest { get; }

    public DateTimeOffset ChangedAtUtc { get; }

    public static ListingMetricsAccessProjection Create(
        Guid listingId,
        Guid actorId,
        bool canViewAnalytics,
        long sourceAggregateRevision,
        string sourcePayloadDigest,
        DateTimeOffset changedAtUtc)
    {
        AnalyticsDomainRules.RequireIdentifier(listingId, nameof(listingId));
        AnalyticsDomainRules.RequireIdentifier(actorId, nameof(actorId));
        if (sourceAggregateRevision <= 0)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_ACCESS_REVISION_INVALID",
                "Listing access source revision must be positive.");
        }

        var normalizedDigest = AnalyticsDomainRules.RequireDigest(
            sourcePayloadDigest,
            nameof(sourcePayloadDigest));
        AnalyticsDomainRules.RequireUtc(changedAtUtc, nameof(changedAtUtc));
        return new ListingMetricsAccessProjection(
            listingId,
            actorId,
            canViewAnalytics,
            sourceAggregateRevision,
            normalizedDigest,
            changedAtUtc);
    }
}

public enum InteractionEventRegistrationState
{
    Stored = 1,
    AlreadyApplied = 2,
    DigestConflict = 3,
}

/// <summary>Returns the exact persisted event selected by one semantic idempotency key.</summary>
public sealed record InteractionEventRegistrationResult(
    InteractionEventRegistrationState State,
    InteractionEvent PersistedEvent);

/// <summary>Persists accepted interaction events with atomic semantic idempotency.</summary>
public interface IAnalyticsEventStore
{
    public Task<InteractionEvent?> GetAsync(
        InteractionEventSemanticKey semanticKey,
        CancellationToken cancellationToken);

    public Task<InteractionEventRegistrationResult> RegisterAsync(
        InteractionEvent interactionEvent,
        CancellationToken cancellationToken);
}

/// <summary>Validates an event against the Analytics-owned projection of public Query membership.</summary>
public interface IPublicReadReferenceStore
{
    public Task<PublicReadMembershipResult> ValidateMembershipAsync(
        Guid publicReadRevisionId,
        string catalogKey,
        Guid? listingId,
        CancellationToken cancellationToken);
}

/// <summary>Persists exact Query public-read activations without synchronously calling Query.</summary>
public interface IPublicReadReferenceProjectionWriter
{
    public Task ApplyAsync(
        PublicReadReferenceProjection projection,
        CancellationToken cancellationToken);
}

/// <summary>Persists Catalog listing access revisions with gap and corruption detection.</summary>
public interface IListingMetricsAccessProjectionWriter
{
    public Task ApplyAsync(
        ListingMetricsAccessProjection projection,
        CancellationToken cancellationToken);
}

/// <summary>Verifies the bounded public anti-abuse proof without persisting its raw token.</summary>
public interface IAntiAbuseVerifier
{
    public Task VerifyAsync(
        string antiAbuseToken,
        Guid clientEventId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);
}

public interface IAnalyticsIdSource
{
    public Guid CreateId();
}

public interface IDailyListingMetricsStore
{
    public Task<IReadOnlyList<DailyListingMetrics>> GetRangeAsync(
        string catalogKey,
        Guid listingId,
        DateOnly fromInclusive,
        DateOnly toExclusive,
        CancellationToken cancellationToken);
}

/// <summary>Authorizes owner metrics through the Analytics-local listing access projection.</summary>
public interface IListingMetricsAuthorizer
{
    public Task AuthorizeAsync(
        Guid actorId,
        Guid listingId,
        CancellationToken cancellationToken);
}
