namespace Aggregator.Query.Contracts;

/// <summary>Routing keys published by the Query owner.</summary>
public static class QueryIntegrationEventTypes
{
    public const string PublicReadRevisionActivated = "query.public-read-revision.activated";
}

/// <summary>Wire contract identities published by the Query owner.</summary>
public static class QueryIntegrationEventContracts
{
    public const string PublicReadRevisionActivated =
        "aggregator.query.public-read-revision-activated@1";
}

/// <summary>Scope identity retained for sponsored attribution in one exact public-read revision.</summary>
public enum PublicReadPlacementScopeTypeContract
{
    Catalog = 1,
    Category = 2,
    District = 3,
    EditorialLanding = 4,
}

/// <summary>Minimal sponsored placement reference needed by Analytics for exact attribution.</summary>
public sealed record PublicReadSponsoredPlacementReference(
    Guid PlacementId,
    Guid ListingId,
    PublicReadPlacementScopeTypeContract ScopeType,
    string ScopeKey,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset HardExpiryAtUtc);

/// <summary>
/// Immutable Query activation projected to Analytics. The payload contains public identities and
/// membership only; it never transports full listing documents or private suppression evidence.
/// </summary>
public sealed record PublicReadRevisionActivated(
    Guid EventId,
    Guid PublicReadRevisionId,
    string CatalogKey,
    long ActivationRevision,
    Guid BaseProjectionId,
    Guid PromotionOverlayId,
    Guid SafetyOverlayId,
    Guid SourcePublicationId,
    string PublicReadContentDigest,
    string MembershipDigest,
    IReadOnlyList<Guid> PublicListingIds,
    IReadOnlyList<PublicReadSponsoredPlacementReference> SponsoredPlacements,
    DateTimeOffset OccurredAtUtc);
