using System.Collections.ObjectModel;

namespace Aggregator.Promotion.Domain;

public enum PlacementScopeType
{
    Catalog = 1,
    Category = 2,
    District = 3,
    EditorialLanding = 4,
}

public enum SponsoredPlacementState
{
    Scheduled = 1,
    Active = 2,
    Paused = 3,
    Ended = 4,
    Revoked = 5,
}

/// <summary>One immutable schedule and presentation revision of a sponsored placement.</summary>
public sealed record SponsoredPlacementRevision
{
    private SponsoredPlacementRevision(
        Guid id,
        Guid placementId,
        long revisionNumber,
        string catalogKey,
        PlacementScopeType scopeType,
        string scopeKey,
        IReadOnlySet<string> localeScope,
        PromotionWindow effectiveWindow,
        int priorityBand,
        int capacitySlot,
        string presentationLabelKey,
        Guid createdByActorId,
        DateTimeOffset createdAtUtc,
        string contentDigest)
    {
        Id = id;
        PlacementId = placementId;
        RevisionNumber = revisionNumber;
        CatalogKey = catalogKey;
        ScopeType = scopeType;
        ScopeKey = scopeKey;
        LocaleScope = localeScope;
        EffectiveWindow = effectiveWindow;
        PriorityBand = priorityBand;
        CapacitySlot = capacitySlot;
        PresentationLabelKey = presentationLabelKey;
        CreatedByActorId = createdByActorId;
        CreatedAtUtc = createdAtUtc;
        ContentDigest = contentDigest;
    }

    public Guid Id { get; }

    public Guid PlacementId { get; }

    public long RevisionNumber { get; }

    public string CatalogKey { get; }

    public PlacementScopeType ScopeType { get; }

    public string ScopeKey { get; }

    public IReadOnlySet<string> LocaleScope { get; }

    public PromotionWindow EffectiveWindow { get; }

    public int PriorityBand { get; }

    public int CapacitySlot { get; }

    public string PresentationLabelKey { get; }

    public Guid CreatedByActorId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string ContentDigest { get; }

    public static SponsoredPlacementRevision Create(
        Guid id,
        Guid placementId,
        long revisionNumber,
        string catalogKey,
        PlacementScopeType scopeType,
        string scopeKey,
        IEnumerable<string> localeScope,
        PromotionWindow effectiveWindow,
        int priorityBand,
        int capacitySlot,
        string presentationLabelKey,
        Guid createdByActorId,
        DateTimeOffset createdAtUtc,
        string contentDigest)
    {
        PromotionDomainRules.RequireIdentifier(id, nameof(id));
        PromotionDomainRules.RequireIdentifier(placementId, nameof(placementId));
        ArgumentOutOfRangeException.ThrowIfLessThan(revisionNumber, 1);
        var normalizedCatalogKey = PromotionDomainRules.RequireKey(catalogKey, nameof(catalogKey));
        if (!Enum.IsDefined(scopeType))
        {
            throw new PromotionDomainException(
                "PROMOTION_SCOPE_TYPE_INVALID",
                $"Placement scope type '{scopeType}' is unsupported.");
        }

        var normalizedScopeKey = PromotionDomainRules.RequireKey(scopeKey, nameof(scopeKey));
        ArgumentNullException.ThrowIfNull(localeScope);
        var normalizedLocales = localeScope
            .Select(value => PromotionDomainRules.RequireLocale(value, nameof(localeScope)))
            .ToHashSet(StringComparer.Ordinal);
        if (normalizedLocales.Count == 0)
        {
            throw new PromotionDomainException(
                "PROMOTION_PLACEMENT_LOCALE_REQUIRED",
                "Sponsored placement must declare at least one locale.");
        }

        ArgumentNullException.ThrowIfNull(effectiveWindow);
        if (priorityBand is < 0 or > 1000)
        {
            throw new PromotionDomainException(
                "PROMOTION_PRIORITY_BAND_INVALID",
                "Sponsored placement priority band must be between 0 and 1000.");
        }

        if (capacitySlot is < 1 or > 1000)
        {
            throw new PromotionDomainException(
                "PROMOTION_CAPACITY_SLOT_INVALID",
                "Sponsored placement capacity slot must be between 1 and 1000.");
        }

        PromotionDomainRules.RequireIdentifier(createdByActorId, nameof(createdByActorId));
        PromotionDomainRules.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        return new SponsoredPlacementRevision(
            id,
            placementId,
            revisionNumber,
            normalizedCatalogKey,
            scopeType,
            normalizedScopeKey,
            new ReadOnlySet<string>(normalizedLocales),
            effectiveWindow,
            priorityBand,
            capacitySlot,
            PromotionDomainRules.RequireKey(presentationLabelKey, nameof(presentationLabelKey)),
            createdByActorId,
            createdAtUtc,
            PromotionDomainRules.RequireDigest(contentDigest, nameof(contentDigest)));
    }
}

/// <summary>Stable placement identity whose schedule revisions never alter organic Catalog content or rank.</summary>
public sealed class SponsoredPlacement
{
    private SponsoredPlacement(
        Guid id,
        Guid entitlementId,
        Guid listingId,
        string productKey,
        SponsoredPlacementState state,
        SponsoredPlacementRevision currentRevision,
        DateTimeOffset changedAtUtc,
        string auditReason,
        long aggregateRevision)
    {
        Id = id;
        EntitlementId = entitlementId;
        ListingId = listingId;
        ProductKey = productKey;
        State = state;
        CurrentRevision = currentRevision;
        ChangedAtUtc = changedAtUtc;
        AuditReason = auditReason;
        AggregateRevision = aggregateRevision;
    }

    public Guid Id { get; }

    public Guid EntitlementId { get; }

    public Guid ListingId { get; }

    public string ProductKey { get; }

    public SponsoredPlacementState State { get; private set; }

    public SponsoredPlacementRevision CurrentRevision { get; private set; }

    public DateTimeOffset HardExpiryAtUtc => CurrentRevision.EffectiveWindow.EndsAtUtc;

    public DateTimeOffset ChangedAtUtc { get; private set; }

    public string AuditReason { get; private set; }

    public long AggregateRevision { get; private set; }

    public bool ConsumesCapacity => State is SponsoredPlacementState.Scheduled or SponsoredPlacementState.Active;

    public static SponsoredPlacement Create(
        Guid id,
        Guid revisionId,
        PromotionEntitlement entitlement,
        PromotionProduct product,
        ListingPromotionEligibility eligibility,
        string catalogKey,
        PlacementScopeType scopeType,
        string scopeKey,
        IEnumerable<string> localeScope,
        PromotionWindow effectiveWindow,
        int priorityBand,
        int capacitySlot,
        string presentationLabelKey,
        Guid actorId,
        string auditReason,
        DateTimeOffset createdAtUtc,
        string contentDigest)
    {
        PromotionDomainRules.RequireIdentifier(id, nameof(id));
        ArgumentNullException.ThrowIfNull(entitlement);
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(eligibility);
        ArgumentNullException.ThrowIfNull(effectiveWindow);
        PromotionDomainRules.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        EnsureProductAndEntitlement(entitlement, product);
        if (entitlement.State is PromotionEntitlementState.Paused or
            PromotionEntitlementState.Revoked or
            PromotionEntitlementState.Expired)
        {
            throw new PromotionDomainException(
                "PROMOTION_ENTITLEMENT_NOT_USABLE",
                $"Entitlement '{entitlement.Id}' cannot authorize a new placement in state '{entitlement.State}'.");
        }

        if (!entitlement.EffectiveWindow.Contains(effectiveWindow))
        {
            throw new PromotionDomainException(
                "PROMOTION_PLACEMENT_OUTSIDE_ENTITLEMENT",
                "Sponsored placement window must be fully contained in its entitlement window.");
        }

        if (createdAtUtc >= effectiveWindow.EndsAtUtc)
        {
            throw new PromotionDomainException(
                "PROMOTION_PLACEMENT_ALREADY_ENDED",
                "A new sponsored placement cannot end at or before its creation time.");
        }

        if (eligibility.ListingId != entitlement.ListingId)
        {
            throw new PromotionDomainException(
                "PROMOTION_ELIGIBILITY_LISTING_MISMATCH",
                "Promotion eligibility projection belongs to another listing.");
        }

        var normalizedCatalogKey = PromotionDomainRules.RequireKey(catalogKey, nameof(catalogKey));
        if (!string.Equals(eligibility.CatalogKey, normalizedCatalogKey, StringComparison.Ordinal))
        {
            throw new PromotionDomainException(
                "PROMOTION_ELIGIBILITY_CATALOG_MISMATCH",
                "Promotion eligibility projection belongs to another catalog.");
        }

        eligibility.EnsureEligible(product, scopeType, scopeKey);
        if (!product.CurrentRevision.PresentationFeatures.Contains(PromotionPresentationFeature.SponsoredSlot) &&
            !product.CurrentRevision.PresentationFeatures.Contains(PromotionPresentationFeature.FeaturedListing))
        {
            throw new PromotionDomainException(
                "PROMOTION_PRODUCT_PLACEMENT_UNSUPPORTED",
                $"Promotion product '{product.Key}' does not authorize a sponsored placement.");
        }

        var revision = SponsoredPlacementRevision.Create(
            revisionId,
            id,
            1,
            normalizedCatalogKey,
            scopeType,
            scopeKey,
            localeScope,
            effectiveWindow,
            priorityBand,
            capacitySlot,
            presentationLabelKey,
            actorId,
            createdAtUtc,
            contentDigest);
        var state = createdAtUtc < effectiveWindow.StartsAtUtc ||
            entitlement.State == PromotionEntitlementState.Scheduled
                ? SponsoredPlacementState.Scheduled
                : SponsoredPlacementState.Active;
        return new SponsoredPlacement(
            id,
            entitlement.Id,
            entitlement.ListingId,
            entitlement.ProductKey,
            state,
            revision,
            createdAtUtc,
            PromotionDomainRules.RequireText(auditReason, nameof(auditReason), 2000),
            1);
    }

    public SponsoredPlacementRevision Revise(
        long expectedAggregateRevision,
        Guid revisionId,
        PromotionEntitlement entitlement,
        PromotionProduct product,
        ListingPromotionEligibility eligibility,
        PlacementScopeType scopeType,
        string scopeKey,
        IEnumerable<string> localeScope,
        PromotionWindow effectiveWindow,
        int priorityBand,
        int capacitySlot,
        string presentationLabelKey,
        Guid actorId,
        string auditReason,
        DateTimeOffset changedAtUtc,
        string contentDigest)
    {
        RequireMutable(expectedAggregateRevision, actorId, changedAtUtc);
        ArgumentNullException.ThrowIfNull(entitlement);
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(eligibility);
        ArgumentNullException.ThrowIfNull(effectiveWindow);
        EnsureProductAndEntitlement(entitlement, product);
        if (entitlement.Id != EntitlementId || entitlement.ListingId != ListingId)
        {
            throw new PromotionDomainException(
                "PROMOTION_PLACEMENT_ENTITLEMENT_MISMATCH",
                "Placement revision must remain bound to its original entitlement and listing.");
        }

        if (!entitlement.EffectiveWindow.Contains(effectiveWindow))
        {
            throw new PromotionDomainException(
                "PROMOTION_PLACEMENT_OUTSIDE_ENTITLEMENT",
                "Sponsored placement window must be fully contained in its entitlement window.");
        }

        eligibility.EnsureEligible(product, scopeType, scopeKey);
        var revision = SponsoredPlacementRevision.Create(
            revisionId,
            Id,
            CurrentRevision.RevisionNumber + 1,
            CurrentRevision.CatalogKey,
            scopeType,
            scopeKey,
            localeScope,
            effectiveWindow,
            priorityBand,
            capacitySlot,
            presentationLabelKey,
            actorId,
            changedAtUtc,
            contentDigest);
        CurrentRevision = revision;
        ChangedAtUtc = changedAtUtc;
        AuditReason = PromotionDomainRules.RequireText(auditReason, nameof(auditReason), 2000);
        State = changedAtUtc < effectiveWindow.StartsAtUtc
            ? SponsoredPlacementState.Scheduled
            : SponsoredPlacementState.Active;
        AggregateRevision++;
        return revision;
    }

    public bool Overlaps(SponsoredPlacement other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ConsumesCapacity &&
            other.ConsumesCapacity &&
            string.Equals(
                CurrentRevision.CatalogKey,
                other.CurrentRevision.CatalogKey,
                StringComparison.Ordinal) &&
            CurrentRevision.ScopeType == other.CurrentRevision.ScopeType &&
            string.Equals(CurrentRevision.ScopeKey, other.CurrentRevision.ScopeKey, StringComparison.Ordinal) &&
            CurrentRevision.CapacitySlot == other.CurrentRevision.CapacitySlot &&
            CurrentRevision.LocaleScope.Overlaps(other.CurrentRevision.LocaleScope) &&
            CurrentRevision.EffectiveWindow.Overlaps(other.CurrentRevision.EffectiveWindow);
    }

    public void Pause(
        long expectedAggregateRevision,
        Guid actorId,
        string auditReason,
        DateTimeOffset changedAtUtc)
    {
        RequireMutable(expectedAggregateRevision, actorId, changedAtUtc);
        if (State is not (SponsoredPlacementState.Active or SponsoredPlacementState.Scheduled))
        {
            throw InvalidTransition(SponsoredPlacementState.Paused);
        }

        ApplyState(SponsoredPlacementState.Paused, auditReason, changedAtUtc);
    }

    public void Resume(
        long expectedAggregateRevision,
        PromotionEntitlement entitlement,
        ListingPromotionEligibility eligibility,
        PromotionProduct product,
        Guid actorId,
        string auditReason,
        DateTimeOffset changedAtUtc)
    {
        RequireMutable(expectedAggregateRevision, actorId, changedAtUtc);
        if (State != SponsoredPlacementState.Paused)
        {
            throw InvalidTransition(SponsoredPlacementState.Active);
        }

        ArgumentNullException.ThrowIfNull(entitlement);
        ArgumentNullException.ThrowIfNull(eligibility);
        ArgumentNullException.ThrowIfNull(product);
        if (!entitlement.IsEffectiveAt(changedAtUtc))
        {
            throw new PromotionDomainException(
                "PROMOTION_ENTITLEMENT_NOT_EFFECTIVE",
                "Paused placement cannot resume without an effective entitlement.");
        }

        eligibility.EnsureEligible(product, CurrentRevision.ScopeType, CurrentRevision.ScopeKey);
        if (changedAtUtc >= HardExpiryAtUtc)
        {
            ApplyState(SponsoredPlacementState.Ended, auditReason, changedAtUtc);
            return;
        }

        ApplyState(SponsoredPlacementState.Active, auditReason, changedAtUtc);
    }

    public bool SynchronizeTime(long expectedAggregateRevision, DateTimeOffset changedAtUtc)
    {
        PromotionDomainRules.RequireExpectedRevision(
            AggregateRevision,
            expectedAggregateRevision,
            "Sponsored placement");
        PromotionDomainRules.RequireUtc(changedAtUtc, nameof(changedAtUtc));
        if (changedAtUtc < ChangedAtUtc)
        {
            throw new PromotionDomainException(
                "PROMOTION_PLACEMENT_TIME_REGRESSION",
                "Sponsored placement transition time cannot precede its current state timestamp.");
        }

        if (State is SponsoredPlacementState.Paused or SponsoredPlacementState.Ended or SponsoredPlacementState.Revoked)
        {
            return false;
        }

        var target = changedAtUtc >= HardExpiryAtUtc
            ? SponsoredPlacementState.Ended
            : changedAtUtc >= CurrentRevision.EffectiveWindow.StartsAtUtc
                ? SponsoredPlacementState.Active
                : SponsoredPlacementState.Scheduled;
        if (target == State)
        {
            return false;
        }

        ApplyState(target, "owner-scheduled placement transition", changedAtUtc);
        return true;
    }

    public void Revoke(
        long expectedAggregateRevision,
        Guid actorId,
        string auditReason,
        DateTimeOffset changedAtUtc)
    {
        RequireMutable(expectedAggregateRevision, actorId, changedAtUtc);
        if (State is SponsoredPlacementState.Revoked or SponsoredPlacementState.Ended)
        {
            throw InvalidTransition(SponsoredPlacementState.Revoked);
        }

        ApplyState(SponsoredPlacementState.Revoked, auditReason, changedAtUtc);
    }

    public static SponsoredPlacement Restore(
        Guid id,
        Guid entitlementId,
        Guid listingId,
        string productKey,
        SponsoredPlacementState state,
        SponsoredPlacementRevision currentRevision,
        DateTimeOffset changedAtUtc,
        string auditReason,
        long aggregateRevision)
    {
        PromotionDomainRules.RequireIdentifier(id, nameof(id));
        PromotionDomainRules.RequireIdentifier(entitlementId, nameof(entitlementId));
        PromotionDomainRules.RequireIdentifier(listingId, nameof(listingId));
        var normalizedProductKey = PromotionDomainRules.RequireKey(productKey, nameof(productKey));
        if (!Enum.IsDefined(state))
        {
            throw new PromotionDomainException(
                "PROMOTION_PLACEMENT_STATE_INVALID",
                $"Sponsored placement state '{state}' is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(currentRevision);
        if (currentRevision.PlacementId != id)
        {
            throw new PromotionDomainException(
                "PROMOTION_PLACEMENT_REVISION_OWNER_MISMATCH",
                "Sponsored placement revision belongs to another placement identity.");
        }

        PromotionDomainRules.RequireUtc(changedAtUtc, nameof(changedAtUtc));
        ArgumentOutOfRangeException.ThrowIfLessThan(aggregateRevision, 1);
        if (aggregateRevision < currentRevision.RevisionNumber)
        {
            throw new PromotionDomainException(
                "PROMOTION_PLACEMENT_REVISION_INVALID",
                "Sponsored placement aggregate revision cannot trail its current schedule revision.");
        }

        return new SponsoredPlacement(
            id,
            entitlementId,
            listingId,
            normalizedProductKey,
            state,
            currentRevision,
            changedAtUtc,
            PromotionDomainRules.RequireText(auditReason, nameof(auditReason), 2000),
            aggregateRevision);
    }

    private static void EnsureProductAndEntitlement(
        PromotionEntitlement entitlement,
        PromotionProduct product)
    {
        if (!string.Equals(entitlement.ProductKey, product.Key, StringComparison.Ordinal))
        {
            throw new PromotionDomainException(
                "PROMOTION_ENTITLEMENT_PRODUCT_MISMATCH",
                "Promotion entitlement and product identities do not match.");
        }
    }

    private void RequireMutable(
        long expectedAggregateRevision,
        Guid actorId,
        DateTimeOffset changedAtUtc)
    {
        PromotionDomainRules.RequireExpectedRevision(
            AggregateRevision,
            expectedAggregateRevision,
            "Sponsored placement");
        PromotionDomainRules.RequireIdentifier(actorId, nameof(actorId));
        PromotionDomainRules.RequireUtc(changedAtUtc, nameof(changedAtUtc));
        if (changedAtUtc < ChangedAtUtc)
        {
            throw new PromotionDomainException(
                "PROMOTION_PLACEMENT_TIME_REGRESSION",
                "Sponsored placement transition time cannot precede its current state timestamp.");
        }
    }

    private void ApplyState(
        SponsoredPlacementState target,
        string auditReason,
        DateTimeOffset changedAtUtc)
    {
        State = target;
        ChangedAtUtc = changedAtUtc;
        AuditReason = PromotionDomainRules.RequireText(auditReason, nameof(auditReason), 2000);
        AggregateRevision++;
    }

    private PromotionDomainException InvalidTransition(SponsoredPlacementState target) =>
        new(
            "PROMOTION_PLACEMENT_TRANSITION_INVALID",
            $"Sponsored placement cannot transition from '{State}' to '{target}'.");
}
