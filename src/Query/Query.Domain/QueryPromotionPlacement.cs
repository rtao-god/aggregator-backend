namespace Aggregator.Query.Domain;

/// <summary>Query-owned lifecycle state projected from one Promotion placement aggregate.</summary>
public enum QueryPromotionPlacementState
{
    Scheduled = 1,
    Active = 2,
    Paused = 3,
    Ended = 4,
    Revoked = 5,
}

/// <summary>Query-owned placement scope projected from the producer contract.</summary>
public enum QueryPromotionPlacementScope
{
    Catalog = 1,
    Category = 2,
    District = 3,
    EditorialLanding = 4,
}

/// <summary>
/// Minimal local placement state used to materialize immutable promotion overlays without copying
/// or mutating the Catalog listing document.
/// </summary>
public sealed record QueryPromotionPlacement
{
    private QueryPromotionPlacement(
        Guid placementId,
        Guid entitlementId,
        Guid listingId,
        string catalogKey,
        string productKey,
        QueryPromotionPlacementScope scope,
        string scopeKey,
        IReadOnlyList<string> localeScope,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        DateTimeOffset hardExpiryAtUtc,
        int priorityBand,
        int capacitySlot,
        string presentationLabelKey,
        QueryPromotionPlacementState state,
        long aggregateRevision,
        DateTimeOffset occurredAtUtc)
    {
        PlacementId = placementId;
        EntitlementId = entitlementId;
        ListingId = listingId;
        CatalogKey = catalogKey;
        ProductKey = productKey;
        Scope = scope;
        ScopeKey = scopeKey;
        LocaleScope = localeScope;
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
        HardExpiryAtUtc = hardExpiryAtUtc;
        PriorityBand = priorityBand;
        CapacitySlot = capacitySlot;
        PresentationLabelKey = presentationLabelKey;
        State = state;
        AggregateRevision = aggregateRevision;
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid PlacementId { get; }

    public Guid EntitlementId { get; }

    public Guid ListingId { get; }

    public string CatalogKey { get; }

    public string ProductKey { get; }

    public QueryPromotionPlacementScope Scope { get; }

    public string ScopeKey { get; }

    public IReadOnlyList<string> LocaleScope { get; }

    public DateTimeOffset StartsAtUtc { get; }

    public DateTimeOffset EndsAtUtc { get; }

    public DateTimeOffset HardExpiryAtUtc { get; }

    public int PriorityBand { get; }

    public int CapacitySlot { get; }

    public string PresentationLabelKey { get; }

    public QueryPromotionPlacementState State { get; }

    public long AggregateRevision { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public bool IsMaterialized =>
        State is QueryPromotionPlacementState.Scheduled or QueryPromotionPlacementState.Active;

    public bool IsVisibleAt(DateTimeOffset instantUtc)
    {
        var instant = QueryContractRules.RequireUtc(instantUtc, nameof(instantUtc));
        return IsMaterialized && StartsAtUtc <= instant && instant < HardExpiryAtUtc;
    }

    public static QueryPromotionPlacement Create(
        Guid placementId,
        Guid entitlementId,
        Guid listingId,
        string catalogKey,
        string productKey,
        QueryPromotionPlacementScope scope,
        string scopeKey,
        IEnumerable<string> localeScope,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        DateTimeOffset hardExpiryAtUtc,
        int priorityBand,
        int capacitySlot,
        string presentationLabelKey,
        QueryPromotionPlacementState state,
        long aggregateRevision,
        DateTimeOffset occurredAtUtc)
    {
        QueryContractRules.RequireId(placementId, nameof(placementId));
        QueryContractRules.RequireId(entitlementId, nameof(entitlementId));
        QueryContractRules.RequireId(listingId, nameof(listingId));
        if (!Enum.IsDefined(scope))
        {
            throw new QueryDomainException(
                "QUERY_PROMOTION_SCOPE_INVALID",
                $"Promotion placement scope '{scope}' is unsupported.");
        }

        if (!Enum.IsDefined(state))
        {
            throw new QueryDomainException(
                "QUERY_PROMOTION_STATE_INVALID",
                $"Promotion placement state '{state}' is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(localeScope);
        var locales = localeScope
            .Select(value => QueryContractRules.RequireText(value, nameof(localeScope), 35))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (locales.Length == 0)
        {
            throw new QueryDomainException(
                "QUERY_PROMOTION_LOCALE_REQUIRED",
                "Promotion placement must target at least one locale.");
        }

        var starts = QueryContractRules.RequireUtc(startsAtUtc, nameof(startsAtUtc));
        var ends = QueryContractRules.RequireUtc(endsAtUtc, nameof(endsAtUtc));
        var hardExpiry = QueryContractRules.RequireUtc(hardExpiryAtUtc, nameof(hardExpiryAtUtc));
        if (ends <= starts)
        {
            throw new QueryDomainException(
                "QUERY_PROMOTION_WINDOW_INVALID",
                "Promotion placement end must be later than its start.");
        }

        if (hardExpiry <= starts || hardExpiry > ends)
        {
            throw new QueryDomainException(
                "QUERY_PROMOTION_HARD_EXPIRY_INVALID",
                "Promotion hard expiry must be later than the start and no later than the placement end.");
        }

        if (priorityBand < 0 || capacitySlot < 0)
        {
            throw new QueryDomainException(
                "QUERY_PROMOTION_ORDER_INVALID",
                "Promotion priority band and capacity slot cannot be negative.");
        }

        if (aggregateRevision <= 0)
        {
            throw new QueryDomainException(
                "QUERY_PROMOTION_REVISION_INVALID",
                "Promotion placement aggregate revision must be positive.");
        }

        return new QueryPromotionPlacement(
            placementId,
            entitlementId,
            listingId,
            QueryContractRules.RequireKey(catalogKey, nameof(catalogKey)),
            QueryContractRules.RequireKey(productKey, nameof(productKey)),
            scope,
            QueryContractRules.RequireKey(scopeKey, nameof(scopeKey)),
            Array.AsReadOnly(locales),
            starts,
            ends,
            hardExpiry,
            priorityBand,
            capacitySlot,
            QueryContractRules.RequireKey(presentationLabelKey, nameof(presentationLabelKey)),
            state,
            aggregateRevision,
            QueryContractRules.RequireUtc(occurredAtUtc, nameof(occurredAtUtc)));
    }
}
