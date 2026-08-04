namespace Aggregator.Query.Domain;

public enum QueryOverlayKind
{
    Promotion = 1,
    VisibilitySafety = 2,
}

public sealed class QueryBaseProjection
{
    private QueryBaseProjection(
        Guid id,
        string catalogKey,
        Guid sourcePublicationId,
        string sourcePublicationDigest,
        long sourcePublicationSequence,
        string builderIdentity,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<QueryListingDocument> documents,
        string contentDigest)
    {
        Id = id;
        CatalogKey = catalogKey;
        SourcePublicationId = sourcePublicationId;
        SourcePublicationDigest = sourcePublicationDigest;
        SourcePublicationSequence = sourcePublicationSequence;
        BuilderIdentity = builderIdentity;
        CreatedAtUtc = createdAtUtc;
        Documents = documents;
        ContentDigest = contentDigest;
    }

    public Guid Id { get; }

    public string CatalogKey { get; }

    public Guid SourcePublicationId { get; }

    public string SourcePublicationDigest { get; }

    public long SourcePublicationSequence { get; }

    public string BuilderIdentity { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyList<QueryListingDocument> Documents { get; }

    public string ContentDigest { get; }

    public static QueryBaseProjection Create(
        Guid id,
        string catalogKey,
        Guid sourcePublicationId,
        string sourcePublicationDigest,
        long sourcePublicationSequence,
        string builderIdentity,
        DateTimeOffset createdAtUtc,
        IEnumerable<QueryListingDocument> documents,
        string contentDigest)
    {
        QueryContractRules.RequireId(id, nameof(id));
        QueryContractRules.RequireId(sourcePublicationId, nameof(sourcePublicationId));
        if (sourcePublicationSequence <= 0)
        {
            throw new QueryDomainException("QUERY_PUBLICATION_SEQUENCE_INVALID", "Source publication sequence must be positive.");
        }

        ArgumentNullException.ThrowIfNull(documents);
        var documentArray = documents.OrderBy(item => item.ListingId).ToArray();
        if (documentArray.Select(item => item.ListingId).Distinct().Count() != documentArray.Length)
        {
            throw new QueryDomainException("QUERY_LISTING_DUPLICATE", "A base projection cannot contain a listing more than once.");
        }

        var routeSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in documentArray.SelectMany(item => item.Localizations).Select(item => item.RoutePath))
        {
            if (!routeSet.Add(route))
            {
                throw new QueryDomainException("QUERY_ROUTE_DUPLICATE", $"Public route '{route}' is duplicated in the base projection.");
            }
        }

        return new QueryBaseProjection(
            id,
            QueryContractRules.RequireKey(catalogKey, nameof(catalogKey)),
            sourcePublicationId,
            QueryContractRules.RequireDigest(sourcePublicationDigest, nameof(sourcePublicationDigest)),
            sourcePublicationSequence,
            QueryContractRules.RequireText(builderIdentity, nameof(builderIdentity), 200),
            QueryContractRules.RequireUtc(createdAtUtc, nameof(createdAtUtc)),
            Array.AsReadOnly(documentArray),
            QueryContractRules.RequireDigest(contentDigest, nameof(contentDigest)));
    }
}

public sealed record QueryOverlayRevision
{
    private QueryOverlayRevision(
        Guid id,
        string catalogKey,
        QueryOverlayKind kind,
        long sourceRevision,
        DateTimeOffset createdAtUtc,
        string contentDigest)
    {
        Id = id;
        CatalogKey = catalogKey;
        Kind = kind;
        SourceRevision = sourceRevision;
        CreatedAtUtc = createdAtUtc;
        ContentDigest = contentDigest;
    }

    public Guid Id { get; }

    public string CatalogKey { get; }

    public QueryOverlayKind Kind { get; }

    public long SourceRevision { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string ContentDigest { get; }

    public int ItemCount => 0;

    public static QueryOverlayRevision CreateEmpty(
        Guid id,
        string catalogKey,
        QueryOverlayKind kind,
        long sourceRevision,
        DateTimeOffset createdAtUtc,
        string contentDigest)
    {
        QueryContractRules.RequireId(id, nameof(id));
        if (sourceRevision < 0)
        {
            throw new QueryDomainException("QUERY_OVERLAY_SOURCE_REVISION_INVALID", "Overlay source revision cannot be negative.");
        }

        return new QueryOverlayRevision(
            id,
            QueryContractRules.RequireKey(catalogKey, nameof(catalogKey)),
            kind,
            sourceRevision,
            QueryContractRules.RequireUtc(createdAtUtc, nameof(createdAtUtc)),
            QueryContractRules.RequireDigest(contentDigest, nameof(contentDigest)));
    }
}

public sealed record PublicReadRevision
{
    private PublicReadRevision(
        Guid id,
        string catalogKey,
        Guid baseProjectionId,
        Guid promotionOverlayId,
        Guid safetyOverlayId,
        Guid sourcePublicationId,
        DateTimeOffset createdAtUtc,
        string contentDigest)
    {
        Id = id;
        CatalogKey = catalogKey;
        BaseProjectionId = baseProjectionId;
        PromotionOverlayId = promotionOverlayId;
        SafetyOverlayId = safetyOverlayId;
        SourcePublicationId = sourcePublicationId;
        CreatedAtUtc = createdAtUtc;
        ContentDigest = contentDigest;
    }

    public Guid Id { get; }

    public string CatalogKey { get; }

    public Guid BaseProjectionId { get; }

    public Guid PromotionOverlayId { get; }

    public Guid SafetyOverlayId { get; }

    public Guid SourcePublicationId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string ContentDigest { get; }

    public static PublicReadRevision Create(
        Guid id,
        QueryBaseProjection baseProjection,
        QueryOverlayRevision promotionOverlay,
        QueryOverlayRevision safetyOverlay,
        DateTimeOffset createdAtUtc,
        string contentDigest)
    {
        ArgumentNullException.ThrowIfNull(baseProjection);
        ArgumentNullException.ThrowIfNull(promotionOverlay);
        ArgumentNullException.ThrowIfNull(safetyOverlay);
        QueryContractRules.RequireId(id, nameof(id));
        if (promotionOverlay.Kind != QueryOverlayKind.Promotion || safetyOverlay.Kind != QueryOverlayKind.VisibilitySafety)
        {
            throw new QueryDomainException("QUERY_OVERLAY_KIND_INVALID", "A public read revision requires one promotion and one visibility-safety overlay.");
        }

        if (!string.Equals(baseProjection.CatalogKey, promotionOverlay.CatalogKey, StringComparison.Ordinal) ||
            !string.Equals(baseProjection.CatalogKey, safetyOverlay.CatalogKey, StringComparison.Ordinal))
        {
            throw new QueryDomainException("QUERY_COMPONENT_CATALOG_MISMATCH", "All public read components must belong to the same catalog.");
        }

        return new PublicReadRevision(
            id,
            baseProjection.CatalogKey,
            baseProjection.Id,
            promotionOverlay.Id,
            safetyOverlay.Id,
            baseProjection.SourcePublicationId,
            QueryContractRules.RequireUtc(createdAtUtc, nameof(createdAtUtc)),
            QueryContractRules.RequireDigest(contentDigest, nameof(contentDigest)));
    }
}
