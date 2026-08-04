namespace Aggregator.Promotion.Domain;

public enum PromotionEntitlementSourceType
{
    ManualContract = 1,
    ManualTrial = 2,
    AdministrativeGrant = 3,
}

public enum PromotionEntitlementState
{
    Scheduled = 1,
    Active = 2,
    Paused = 3,
    Revoked = 4,
    Expired = 5,
}

/// <summary>Listing-scoped right to use one Promotion product during an exact UTC interval.</summary>
public sealed class PromotionEntitlement
{
    private PromotionEntitlement(
        Guid id,
        Guid listingId,
        string productKey,
        PromotionEntitlementSourceType sourceType,
        string externalReference,
        PromotionWindow effectiveWindow,
        PromotionEntitlementState state,
        Guid createdByActorId,
        string auditReason,
        DateTimeOffset createdAtUtc,
        DateTimeOffset changedAtUtc,
        long aggregateRevision)
    {
        Id = id;
        ListingId = listingId;
        ProductKey = productKey;
        SourceType = sourceType;
        ExternalReference = externalReference;
        EffectiveWindow = effectiveWindow;
        State = state;
        CreatedByActorId = createdByActorId;
        AuditReason = auditReason;
        CreatedAtUtc = createdAtUtc;
        ChangedAtUtc = changedAtUtc;
        AggregateRevision = aggregateRevision;
    }

    public Guid Id { get; }

    public Guid ListingId { get; }

    public string ProductKey { get; }

    public PromotionEntitlementSourceType SourceType { get; }

    public string ExternalReference { get; }

    public PromotionWindow EffectiveWindow { get; }

    public PromotionEntitlementState State { get; private set; }

    public Guid CreatedByActorId { get; }

    public string AuditReason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset ChangedAtUtc { get; private set; }

    public long AggregateRevision { get; private set; }

    public static PromotionEntitlement Grant(
        Guid id,
        Guid listingId,
        string productKey,
        PromotionEntitlementSourceType sourceType,
        string externalReference,
        PromotionWindow effectiveWindow,
        Guid actorId,
        string auditReason,
        DateTimeOffset createdAtUtc)
    {
        PromotionDomainRules.RequireIdentifier(id, nameof(id));
        PromotionDomainRules.RequireIdentifier(listingId, nameof(listingId));
        var normalizedProductKey = PromotionDomainRules.RequireKey(productKey, nameof(productKey));
        if (!Enum.IsDefined(sourceType))
        {
            throw new PromotionDomainException(
                "PROMOTION_ENTITLEMENT_SOURCE_INVALID",
                $"Promotion entitlement source '{sourceType}' is unsupported.");
        }

        var normalizedReference = PromotionDomainRules.RequireText(
            externalReference,
            nameof(externalReference),
            500);
        ArgumentNullException.ThrowIfNull(effectiveWindow);
        PromotionDomainRules.RequireIdentifier(actorId, nameof(actorId));
        PromotionDomainRules.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        if (createdAtUtc >= effectiveWindow.EndsAtUtc)
        {
            throw new PromotionDomainException(
                "PROMOTION_ENTITLEMENT_ALREADY_EXPIRED",
                "A new Promotion entitlement cannot end at or before its creation time.");
        }

        var state = createdAtUtc < effectiveWindow.StartsAtUtc
            ? PromotionEntitlementState.Scheduled
            : PromotionEntitlementState.Active;
        return new PromotionEntitlement(
            id,
            listingId,
            normalizedProductKey,
            sourceType,
            normalizedReference,
            effectiveWindow,
            state,
            actorId,
            PromotionDomainRules.RequireText(auditReason, nameof(auditReason), 2000),
            createdAtUtc,
            createdAtUtc,
            1);
    }

    public bool IsEffectiveAt(DateTimeOffset timestampUtc)
    {
        PromotionDomainRules.RequireUtc(timestampUtc, nameof(timestampUtc));
        return State == PromotionEntitlementState.Active && EffectiveWindow.Contains(timestampUtc);
    }

    public void Pause(
        long expectedAggregateRevision,
        Guid actorId,
        string auditReason,
        DateTimeOffset changedAtUtc)
    {
        RequireMutable(expectedAggregateRevision, actorId, changedAtUtc);
        if (State is not (PromotionEntitlementState.Active or PromotionEntitlementState.Scheduled))
        {
            throw InvalidTransition(PromotionEntitlementState.Paused);
        }

        Apply(PromotionEntitlementState.Paused, auditReason, changedAtUtc);
    }

    public void Resume(
        long expectedAggregateRevision,
        Guid actorId,
        string auditReason,
        DateTimeOffset changedAtUtc)
    {
        RequireMutable(expectedAggregateRevision, actorId, changedAtUtc);
        if (State != PromotionEntitlementState.Paused)
        {
            throw InvalidTransition(PromotionEntitlementState.Active);
        }

        if (changedAtUtc >= EffectiveWindow.EndsAtUtc)
        {
            Apply(PromotionEntitlementState.Expired, auditReason, changedAtUtc);
            return;
        }

        var target = changedAtUtc < EffectiveWindow.StartsAtUtc
            ? PromotionEntitlementState.Scheduled
            : PromotionEntitlementState.Active;
        Apply(target, auditReason, changedAtUtc);
    }

    public void Revoke(
        long expectedAggregateRevision,
        Guid actorId,
        string auditReason,
        DateTimeOffset changedAtUtc)
    {
        RequireMutable(expectedAggregateRevision, actorId, changedAtUtc);
        if (State is PromotionEntitlementState.Revoked or PromotionEntitlementState.Expired)
        {
            throw InvalidTransition(PromotionEntitlementState.Revoked);
        }

        Apply(PromotionEntitlementState.Revoked, auditReason, changedAtUtc);
    }

    public bool SynchronizeTime(
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        PromotionDomainRules.RequireExpectedRevision(
            AggregateRevision,
            expectedAggregateRevision,
            "Promotion entitlement");
        PromotionDomainRules.RequireUtc(changedAtUtc, nameof(changedAtUtc));
        if (changedAtUtc < ChangedAtUtc)
        {
            throw new PromotionDomainException(
                "PROMOTION_ENTITLEMENT_TIME_REGRESSION",
                "Promotion entitlement transition time cannot precede its current state timestamp.");
        }

        if (State is PromotionEntitlementState.Revoked or PromotionEntitlementState.Expired or PromotionEntitlementState.Paused)
        {
            return false;
        }

        var target = changedAtUtc >= EffectiveWindow.EndsAtUtc
            ? PromotionEntitlementState.Expired
            : changedAtUtc >= EffectiveWindow.StartsAtUtc
                ? PromotionEntitlementState.Active
                : PromotionEntitlementState.Scheduled;
        if (target == State)
        {
            return false;
        }

        Apply(target, "owner-scheduled entitlement transition", changedAtUtc);
        return true;
    }

    public static PromotionEntitlement Restore(
        Guid id,
        Guid listingId,
        string productKey,
        PromotionEntitlementSourceType sourceType,
        string externalReference,
        PromotionWindow effectiveWindow,
        PromotionEntitlementState state,
        Guid createdByActorId,
        string auditReason,
        DateTimeOffset createdAtUtc,
        DateTimeOffset changedAtUtc,
        long aggregateRevision)
    {
        PromotionDomainRules.RequireIdentifier(id, nameof(id));
        PromotionDomainRules.RequireIdentifier(listingId, nameof(listingId));
        var normalizedProductKey = PromotionDomainRules.RequireKey(productKey, nameof(productKey));
        if (!Enum.IsDefined(sourceType) || !Enum.IsDefined(state))
        {
            throw new PromotionDomainException(
                "PROMOTION_ENTITLEMENT_STATE_INVALID",
                "Stored Promotion entitlement contains an unsupported enum value.");
        }

        ArgumentNullException.ThrowIfNull(effectiveWindow);
        PromotionDomainRules.RequireIdentifier(createdByActorId, nameof(createdByActorId));
        PromotionDomainRules.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        PromotionDomainRules.RequireUtc(changedAtUtc, nameof(changedAtUtc));
        if (changedAtUtc < createdAtUtc)
        {
            throw new PromotionDomainException(
                "PROMOTION_ENTITLEMENT_TIME_INVALID",
                "Promotion entitlement change time cannot precede creation time.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(aggregateRevision, 1);
        return new PromotionEntitlement(
            id,
            listingId,
            normalizedProductKey,
            sourceType,
            PromotionDomainRules.RequireText(externalReference, nameof(externalReference), 500),
            effectiveWindow,
            state,
            createdByActorId,
            PromotionDomainRules.RequireText(auditReason, nameof(auditReason), 2000),
            createdAtUtc,
            changedAtUtc,
            aggregateRevision);
    }

    private void RequireMutable(
        long expectedAggregateRevision,
        Guid actorId,
        DateTimeOffset changedAtUtc)
    {
        PromotionDomainRules.RequireExpectedRevision(
            AggregateRevision,
            expectedAggregateRevision,
            "Promotion entitlement");
        PromotionDomainRules.RequireIdentifier(actorId, nameof(actorId));
        PromotionDomainRules.RequireUtc(changedAtUtc, nameof(changedAtUtc));
        if (changedAtUtc < ChangedAtUtc)
        {
            throw new PromotionDomainException(
                "PROMOTION_ENTITLEMENT_TIME_REGRESSION",
                "Promotion entitlement transition time cannot precede its current state timestamp.");
        }
    }

    private void Apply(
        PromotionEntitlementState target,
        string auditReason,
        DateTimeOffset changedAtUtc)
    {
        State = target;
        AuditReason = PromotionDomainRules.RequireText(auditReason, nameof(auditReason), 2000);
        ChangedAtUtc = changedAtUtc;
        AggregateRevision++;
    }

    private PromotionDomainException InvalidTransition(PromotionEntitlementState target) =>
        new(
            "PROMOTION_ENTITLEMENT_TRANSITION_INVALID",
            $"Promotion entitlement cannot transition from '{State}' to '{target}'.");
}
