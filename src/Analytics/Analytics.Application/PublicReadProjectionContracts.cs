using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

public enum PublicReadMembershipState
{
    Known = 1,
    UnknownRevision = 2,
    CatalogMismatch = 3,
    ListingNotPublic = 4,
    ListingRequired = 5,
    SponsoredPlacementNotPublic = 6,
    SponsoredPlacementListingMismatch = 7,
    SponsoredPlacementScopeMismatch = 8,
    SponsoredPlacementInactive = 9,
}

public sealed record PublicReadMembershipResult(
    PublicReadMembershipState State,
    string? ActualCatalogKey,
    Guid? ActualListingId,
    Guid? ActualPlacementId = null,
    Guid? ActualPlacementListingId = null,
    string? ActualPlacementScopeKey = null);

/// <summary>Query-owned placement scope projected locally for exact Analytics attribution.</summary>
public enum PublicReadSponsoredPlacementScope
{
    Catalog = 1,
    Category = 2,
    District = 3,
    EditorialLanding = 4,
}

/// <summary>Minimal sponsored placement identity retained for one exact public-read revision.</summary>
public sealed record PublicReadSponsoredPlacementProjection
{
    private PublicReadSponsoredPlacementProjection(
        Guid placementId,
        Guid listingId,
        PublicReadSponsoredPlacementScope scopeType,
        string scopeKey,
        DateTimeOffset startsAtUtc,
        DateTimeOffset hardExpiryAtUtc)
    {
        PlacementId = placementId;
        ListingId = listingId;
        ScopeType = scopeType;
        ScopeKey = scopeKey;
        StartsAtUtc = startsAtUtc;
        HardExpiryAtUtc = hardExpiryAtUtc;
    }

    public Guid PlacementId { get; }

    public Guid ListingId { get; }

    public PublicReadSponsoredPlacementScope ScopeType { get; }

    public string ScopeKey { get; }

    public DateTimeOffset StartsAtUtc { get; }

    public DateTimeOffset HardExpiryAtUtc { get; }

    public static PublicReadSponsoredPlacementProjection Create(
        Guid placementId,
        Guid listingId,
        PublicReadSponsoredPlacementScope scopeType,
        string scopeKey,
        DateTimeOffset startsAtUtc,
        DateTimeOffset hardExpiryAtUtc)
    {
        AnalyticsDomainRules.RequireIdentifier(placementId, nameof(placementId));
        AnalyticsDomainRules.RequireIdentifier(listingId, nameof(listingId));
        if (!Enum.IsDefined(scopeType))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_PLACEMENT_SCOPE_INVALID",
                $"Public sponsored placement scope '{scopeType}' is unsupported.");
        }

        var normalizedScopeKey = AnalyticsDomainRules.RequireKey(
            scopeKey,
            nameof(scopeKey),
            maximumLength: 200);
        AnalyticsDomainRules.RequireUtc(startsAtUtc, nameof(startsAtUtc));
        AnalyticsDomainRules.RequireUtc(hardExpiryAtUtc, nameof(hardExpiryAtUtc));
        if (startsAtUtc >= hardExpiryAtUtc)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_PLACEMENT_INTERVAL_INVALID",
                "Public sponsored placement start must precede its hard expiry.");
        }

        return new PublicReadSponsoredPlacementProjection(
            placementId,
            listingId,
            scopeType,
            normalizedScopeKey,
            startsAtUtc,
            hardExpiryAtUtc);
    }
}

/// <summary>One immutable Query activation projected into Analytics for local event validation.</summary>
public sealed record PublicReadReferenceProjection
{
    private PublicReadReferenceProjection(
        Guid publicReadRevisionId,
        string catalogKey,
        long activationRevision,
        Guid baseProjectionId,
        Guid promotionOverlayId,
        Guid safetyOverlayId,
        Guid sourcePublicationId,
        string publicReadContentDigest,
        string membershipDigest,
        string projectionDigest,
        DateTimeOffset activatedAtUtc,
        IReadOnlyList<Guid> publicListingIds,
        IReadOnlyList<PublicReadSponsoredPlacementProjection> sponsoredPlacements)
    {
        PublicReadRevisionId = publicReadRevisionId;
        CatalogKey = catalogKey;
        ActivationRevision = activationRevision;
        BaseProjectionId = baseProjectionId;
        PromotionOverlayId = promotionOverlayId;
        SafetyOverlayId = safetyOverlayId;
        SourcePublicationId = sourcePublicationId;
        PublicReadContentDigest = publicReadContentDigest;
        MembershipDigest = membershipDigest;
        ProjectionDigest = projectionDigest;
        ActivatedAtUtc = activatedAtUtc;
        PublicListingIds = publicListingIds;
        SponsoredPlacements = sponsoredPlacements;
    }

    public Guid PublicReadRevisionId { get; }

    public string CatalogKey { get; }

    public long ActivationRevision { get; }

    public Guid BaseProjectionId { get; }

    public Guid PromotionOverlayId { get; }

    public Guid SafetyOverlayId { get; }

    public Guid SourcePublicationId { get; }

    public string PublicReadContentDigest { get; }

    public string MembershipDigest { get; }

    public string ProjectionDigest { get; }

    public DateTimeOffset ActivatedAtUtc { get; }

    public IReadOnlyList<Guid> PublicListingIds { get; }

    public IReadOnlyList<PublicReadSponsoredPlacementProjection> SponsoredPlacements { get; }

    public static PublicReadReferenceProjection Create(
        Guid publicReadRevisionId,
        string catalogKey,
        long activationRevision,
        Guid baseProjectionId,
        Guid promotionOverlayId,
        Guid safetyOverlayId,
        Guid sourcePublicationId,
        string publicReadContentDigest,
        string membershipDigest,
        DateTimeOffset activatedAtUtc,
        IEnumerable<Guid> publicListingIds,
        IEnumerable<PublicReadSponsoredPlacementProjection>? sponsoredPlacements = null)
    {
        AnalyticsDomainRules.RequireIdentifier(publicReadRevisionId, nameof(publicReadRevisionId));
        var normalizedCatalogKey = AnalyticsDomainRules.RequireKey(catalogKey, nameof(catalogKey));
        if (activationRevision <= 0)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_ACTIVATION_REVISION_INVALID",
                "Public-read activation revision must be positive.");
        }

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

        var placements = (sponsoredPlacements ?? Array.Empty<PublicReadSponsoredPlacementProjection>())
            .OrderBy(placement => placement.PlacementId)
            .ToArray();
        if (placements.Any(placement => placement is null))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_PLACEMENT_REQUIRED",
                "Public-read sponsored placement membership cannot contain an empty item.");
        }

        if (placements.Select(placement => placement.PlacementId).Distinct().Count() != placements.Length)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_PLACEMENT_DUPLICATE",
                "Public-read sponsored placement membership cannot contain duplicate placement IDs.");
        }

        var listingMembership = listingIds.ToHashSet();
        var foreignPlacement = placements.FirstOrDefault(
            placement => !listingMembership.Contains(placement.ListingId));
        if (foreignPlacement is not null)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_PLACEMENT_LISTING_NOT_PUBLIC",
                $"Sponsored placement '{foreignPlacement.PlacementId}' references listing '{foreignPlacement.ListingId}' outside the public membership.");
        }

        var readonlyListingIds = Array.AsReadOnly(listingIds);
        var readonlyPlacements = Array.AsReadOnly(placements);
        var projectionDigest = AnalyticsCanonicalJson.ComputeDigest(new
        {
            PublicReadRevisionId = publicReadRevisionId,
            CatalogKey = normalizedCatalogKey,
            ActivationRevision = activationRevision,
            BaseProjectionId = baseProjectionId,
            PromotionOverlayId = promotionOverlayId,
            SafetyOverlayId = safetyOverlayId,
            SourcePublicationId = sourcePublicationId,
            PublicReadContentDigest = normalizedContentDigest,
            MembershipDigest = normalizedMembershipDigest,
            ActivatedAtUtc = activatedAtUtc,
            PublicListingIds = readonlyListingIds,
            SponsoredPlacements = readonlyPlacements,
        });
        return new PublicReadReferenceProjection(
            publicReadRevisionId,
            normalizedCatalogKey,
            activationRevision,
            baseProjectionId,
            promotionOverlayId,
            safetyOverlayId,
            sourcePublicationId,
            normalizedContentDigest,
            normalizedMembershipDigest,
            projectionDigest,
            activatedAtUtc,
            readonlyListingIds,
            readonlyPlacements);
    }
}

/// <summary>Transport metadata required for atomic Query-event inbox processing.</summary>
public sealed record PublicReadActivationInboxMessage
{
    private PublicReadActivationInboxMessage(
        Guid eventId,
        string routingKey,
        string contractIdentity,
        string payloadDigest,
        long activationRevision,
        DateTimeOffset receivedAtUtc,
        string correlationId)
    {
        EventId = eventId;
        RoutingKey = routingKey;
        ContractIdentity = contractIdentity;
        PayloadDigest = payloadDigest;
        ActivationRevision = activationRevision;
        ReceivedAtUtc = receivedAtUtc;
        CorrelationId = correlationId;
    }

    public Guid EventId { get; }

    public string RoutingKey { get; }

    public string ContractIdentity { get; }

    public string PayloadDigest { get; }

    public long ActivationRevision { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public string CorrelationId { get; }

    public static PublicReadActivationInboxMessage Create(
        Guid eventId,
        string routingKey,
        string contractIdentity,
        string payloadDigest,
        long activationRevision,
        DateTimeOffset receivedAtUtc,
        string correlationId)
    {
        AnalyticsDomainRules.RequireIdentifier(eventId, nameof(eventId));
        var normalizedRoutingKey = RequireTransportValue(
            routingKey,
            nameof(routingKey),
            maximumLength: 200);
        var normalizedContractIdentity = RequireTransportValue(
            contractIdentity,
            nameof(contractIdentity),
            maximumLength: 200);
        var normalizedPayloadDigest = AnalyticsDomainRules.RequireDigest(
            payloadDigest,
            nameof(payloadDigest));
        if (activationRevision <= 0)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_ACTIVATION_REVISION_INVALID",
                "Public-read activation revision must be positive.");
        }

        AnalyticsDomainRules.RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        var normalizedCorrelationId = RequireTransportValue(
            correlationId,
            nameof(correlationId),
            maximumLength: 128);
        return new PublicReadActivationInboxMessage(
            eventId,
            normalizedRoutingKey,
            normalizedContractIdentity,
            normalizedPayloadDigest,
            activationRevision,
            receivedAtUtc,
            normalizedCorrelationId);
    }

    private static string RequireTransportValue(
        string value,
        string parameterName,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_MESSAGE_METADATA_INVALID",
                $"'{parameterName}' must contain between 1 and {maximumLength} characters.");
        }

        return value.Trim();
    }
}

public enum PublicReadActivationDisposition
{
    Applied = 1,
    Replayed = 2,
    IgnoredStale = 3,
}

public sealed record PublicReadActivationProjectionResult(
    PublicReadReferenceProjection Projection,
    PublicReadActivationDisposition Disposition);

/// <summary>Applies one Query activation with inbox, revision, and projection state atomically.</summary>
public interface IPublicReadActivationProjectionStore
{
    public Task<PublicReadActivationProjectionResult> ApplyAsync(
        PublicReadReferenceProjection projection,
        PublicReadActivationInboxMessage inboxMessage,
        CancellationToken cancellationToken);
}
