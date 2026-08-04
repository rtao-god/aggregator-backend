using System.Text.RegularExpressions;

namespace Aggregator.Promotion.Domain;

public enum PromotionCampaignState
{
    Draft = 1,
    Active = 2,
    Suspended = 3,
    Completed = 4,
    Cancelled = 5,
}

/// <summary>Owns one explicitly sponsored placement without changing organic ranking.</summary>
public sealed class PromotionCampaign
{
    private static readonly Regex KeyRegex = new(
        "^[a-z][a-z0-9-]{0,95}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private PromotionCampaign(
        Guid id,
        Guid productRevisionId,
        Guid entitlementId,
        Guid listingId,
        string catalogKey,
        string placementKey,
        int capacityUnits,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        ProductRevisionId = productRevisionId;
        EntitlementId = entitlementId;
        ListingId = listingId;
        CatalogKey = catalogKey;
        PlacementKey = placementKey;
        CapacityUnits = capacityUnits;
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
        CreatedAtUtc = createdAtUtc;
        LastChangedAtUtc = createdAtUtc;
        State = PromotionCampaignState.Draft;
        AggregateRevision = 1;
    }

    public Guid Id { get; }

    public Guid ProductRevisionId { get; }

    public Guid EntitlementId { get; }

    public Guid ListingId { get; }

    public string CatalogKey { get; }

    public string PlacementKey { get; }

    public int CapacityUnits { get; }

    public DateTimeOffset StartsAtUtc { get; }

    public DateTimeOffset EndsAtUtc { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastChangedAtUtc { get; private set; }

    public PromotionCampaignState State { get; private set; }

    public long AggregateRevision { get; private set; }

    public string? SuspensionReason { get; private set; }

    public static PromotionCampaign Create(
        Guid id,
        Guid productRevisionId,
        Guid entitlementId,
        Guid listingId,
        string catalogKey,
        string placementKey,
        int capacityUnits,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        DateTimeOffset createdAtUtc)
    {
        RequireId(id, nameof(id));
        RequireId(productRevisionId, nameof(productRevisionId));
        RequireId(entitlementId, nameof(entitlementId));
        RequireId(listingId, nameof(listingId));
        RequireKey(catalogKey, nameof(catalogKey));
        RequireKey(placementKey, nameof(placementKey));
        if (capacityUnits is < 1 or > 100)
        {
            throw new PromotionCampaignException(
                "PROMOTION_CAPACITY_UNITS_INVALID",
                "Promotion capacity units must be between 1 and 100.");
        }

        RequireUtc(startsAtUtc, nameof(startsAtUtc));
        RequireUtc(endsAtUtc, nameof(endsAtUtc));
        RequireUtc(createdAtUtc, nameof(createdAtUtc));
        if (endsAtUtc <= startsAtUtc)
        {
            throw new PromotionCampaignException(
                "PROMOTION_WINDOW_INVALID",
                "A promotion campaign must end after it starts.");
        }

        if (endsAtUtc - startsAtUtc > TimeSpan.FromDays(366))
        {
            throw new PromotionCampaignException(
                "PROMOTION_WINDOW_TOO_LONG",
                "A promotion campaign window cannot exceed 366 days.");
        }

        return new PromotionCampaign(
            id,
            productRevisionId,
            entitlementId,
            listingId,
            catalogKey,
            placementKey,
            capacityUnits,
            startsAtUtc,
            endsAtUtc,
            createdAtUtc);
    }

    public static PromotionCampaign Restore(
        Guid id,
        Guid productRevisionId,
        Guid entitlementId,
        Guid listingId,
        string catalogKey,
        string placementKey,
        int capacityUnits,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        DateTimeOffset createdAtUtc,
        DateTimeOffset lastChangedAtUtc,
        PromotionCampaignState state,
        long aggregateRevision,
        string? suspensionReason)
    {
        var campaign = Create(
            id,
            productRevisionId,
            entitlementId,
            listingId,
            catalogKey,
            placementKey,
            capacityUnits,
            startsAtUtc,
            endsAtUtc,
            createdAtUtc);
        RequireUtc(lastChangedAtUtc, nameof(lastChangedAtUtc));
        if (lastChangedAtUtc < createdAtUtc)
        {
            throw new PromotionCampaignException(
                "PROMOTION_TIME_REGRESSION",
                "A persisted campaign cannot change before it was created.");
        }

        if (!Enum.IsDefined(state) || aggregateRevision < MinimumRevision(state))
        {
            throw new PromotionCampaignException(
                "PROMOTION_PERSISTED_STATE_INVALID",
                "The persisted campaign state and aggregate revision are inconsistent.");
        }

        if (state == PromotionCampaignState.Suspended && string.IsNullOrWhiteSpace(suspensionReason))
        {
            throw new PromotionCampaignException(
                "PROMOTION_SUSPENSION_REASON_REQUIRED",
                "A suspended campaign requires an explicit reason.");
        }

        if (state != PromotionCampaignState.Suspended && suspensionReason is not null)
        {
            throw new PromotionCampaignException(
                "PROMOTION_SUSPENSION_REASON_INVALID",
                "Only a suspended campaign may retain a suspension reason.");
        }

        campaign.LastChangedAtUtc = lastChangedAtUtc;
        campaign.State = state;
        campaign.AggregateRevision = aggregateRevision;
        campaign.SuspensionReason = suspensionReason;
        return campaign;
    }

    public void Activate(
        bool productRevisionActive,
        bool entitlementActive,
        bool listingEligible,
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        EnsureState(PromotionCampaignState.Draft, expectedAggregateRevision);
        if (!productRevisionActive || !entitlementActive || !listingEligible)
        {
            throw new PromotionCampaignException(
                "PROMOTION_ACTIVATION_NOT_ELIGIBLE",
                "A campaign requires an active product revision, active entitlement and eligible listing.");
        }

        if (changedAtUtc >= EndsAtUtc)
        {
            throw new PromotionCampaignException(
                "PROMOTION_ACTIVATION_AFTER_WINDOW",
                "An expired promotion window cannot be activated.");
        }

        Apply(PromotionCampaignState.Active, changedAtUtc);
    }

    public void Suspend(
        string reason,
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        EnsureState(PromotionCampaignState.Active, expectedAggregateRevision);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 300)
        {
            throw new PromotionCampaignException(
                "PROMOTION_SUSPENSION_REASON_INVALID",
                "A bounded suspension reason is required.");
        }

        SuspensionReason = reason.Trim();
        Apply(PromotionCampaignState.Suspended, changedAtUtc);
    }

    public void Resume(
        bool productRevisionActive,
        bool entitlementActive,
        bool listingEligible,
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        EnsureState(PromotionCampaignState.Suspended, expectedAggregateRevision);
        if (!productRevisionActive || !entitlementActive || !listingEligible)
        {
            throw new PromotionCampaignException(
                "PROMOTION_RESUME_NOT_ELIGIBLE",
                "A suspended campaign cannot resume while any eligibility owner is inactive.");
        }

        if (changedAtUtc >= EndsAtUtc)
        {
            throw new PromotionCampaignException(
                "PROMOTION_RESUME_AFTER_WINDOW",
                "An expired promotion window cannot resume.");
        }

        SuspensionReason = null;
        Apply(PromotionCampaignState.Active, changedAtUtc);
    }

    public void Complete(long expectedAggregateRevision, DateTimeOffset changedAtUtc)
    {
        EnsureRevision(expectedAggregateRevision);
        if (State is not PromotionCampaignState.Active and not PromotionCampaignState.Suspended)
        {
            throw new PromotionCampaignException(
                "PROMOTION_COMPLETE_STATE_INVALID",
                $"Campaign state '{State}' cannot complete.");
        }

        if (changedAtUtc < EndsAtUtc)
        {
            throw new PromotionCampaignException(
                "PROMOTION_COMPLETE_BEFORE_WINDOW_END",
                "A campaign cannot complete before its exact window ends.");
        }

        SuspensionReason = null;
        Apply(PromotionCampaignState.Completed, changedAtUtc);
    }

    public void Cancel(long expectedAggregateRevision, DateTimeOffset changedAtUtc)
    {
        EnsureRevision(expectedAggregateRevision);
        if (State is PromotionCampaignState.Completed or PromotionCampaignState.Cancelled)
        {
            throw new PromotionCampaignException(
                "PROMOTION_CANCEL_STATE_INVALID",
                $"Campaign state '{State}' cannot be cancelled.");
        }

        SuspensionReason = null;
        Apply(PromotionCampaignState.Cancelled, changedAtUtc);
    }

    private void EnsureState(PromotionCampaignState state, long expectedAggregateRevision)
    {
        EnsureRevision(expectedAggregateRevision);
        if (State != state)
        {
            throw new PromotionCampaignException(
                "PROMOTION_STATE_INVALID",
                $"Campaign state '{State}' cannot execute a transition requiring '{state}'.");
        }
    }

    private void EnsureRevision(long expectedAggregateRevision)
    {
        if (expectedAggregateRevision != AggregateRevision)
        {
            throw new PromotionCampaignException(
                "PROMOTION_REVISION_CONFLICT",
                $"Expected campaign revision {expectedAggregateRevision}, actual revision {AggregateRevision}.");
        }
    }

    private void Apply(PromotionCampaignState state, DateTimeOffset changedAtUtc)
    {
        RequireUtc(changedAtUtc, nameof(changedAtUtc));
        if (changedAtUtc < LastChangedAtUtc)
        {
            throw new PromotionCampaignException(
                "PROMOTION_TIME_REGRESSION",
                "A campaign transition cannot move to an earlier timestamp.");
        }

        State = state;
        LastChangedAtUtc = changedAtUtc;
        AggregateRevision++;
    }

    private static long MinimumRevision(PromotionCampaignState state) => state switch
    {
        PromotionCampaignState.Draft => 1,
        PromotionCampaignState.Active or PromotionCampaignState.Cancelled => 2,
        PromotionCampaignState.Suspended => 3,
        PromotionCampaignState.Completed => 3,
        _ => throw new PromotionCampaignException(
            "PROMOTION_STATE_INVALID",
            "The promotion campaign state is unsupported."),
    };

    private static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new PromotionCampaignException(
                "PROMOTION_ID_REQUIRED",
                $"A non-empty {name} is required.");
        }
    }

    private static void RequireKey(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !KeyRegex.IsMatch(value))
        {
            throw new PromotionCampaignException(
                "PROMOTION_KEY_INVALID",
                $"{name} must be a lowercase semantic key.");
        }
    }

    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new PromotionCampaignException(
                "PROMOTION_TIME_NOT_UTC",
                $"{name} must use UTC.");
        }
    }
}

public sealed class PromotionCampaignException : InvalidOperationException
{
    public PromotionCampaignException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}
