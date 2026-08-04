using System.Collections.ObjectModel;

namespace Aggregator.Promotion.Domain;

public enum PromotionProductState
{
    Active = 1,
    Inactive = 2,
    Archived = 3,
}

public enum PromotionPresentationFeature
{
    FeaturedListing = 1,
    SponsoredSlot = 2,
    ExtendedCard = 3,
    ExtendedGallery = 4,
}

/// <summary>One immutable semantic revision of a Promotion product.</summary>
public sealed record PromotionProductRevision
{
    private PromotionProductRevision(
        Guid id,
        Guid productId,
        long revisionNumber,
        IReadOnlyDictionary<string, string> displayNames,
        IReadOnlySet<PromotionPresentationFeature> presentationFeatures,
        bool requiresVerifiedContact,
        string? requiredContactCapability,
        Guid createdByActorId,
        DateTimeOffset createdAtUtc,
        string contentDigest)
    {
        Id = id;
        ProductId = productId;
        RevisionNumber = revisionNumber;
        DisplayNames = displayNames;
        PresentationFeatures = presentationFeatures;
        RequiresVerifiedContact = requiresVerifiedContact;
        RequiredContactCapability = requiredContactCapability;
        CreatedByActorId = createdByActorId;
        CreatedAtUtc = createdAtUtc;
        ContentDigest = contentDigest;
    }

    public Guid Id { get; }

    public Guid ProductId { get; }

    public long RevisionNumber { get; }

    public IReadOnlyDictionary<string, string> DisplayNames { get; }

    public IReadOnlySet<PromotionPresentationFeature> PresentationFeatures { get; }

    public bool RequiresVerifiedContact { get; }

    public string? RequiredContactCapability { get; }

    public Guid CreatedByActorId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string ContentDigest { get; }

    public static PromotionProductRevision Create(
        Guid id,
        Guid productId,
        long revisionNumber,
        IReadOnlyDictionary<string, string> displayNames,
        IEnumerable<PromotionPresentationFeature> presentationFeatures,
        bool requiresVerifiedContact,
        string? requiredContactCapability,
        Guid createdByActorId,
        DateTimeOffset createdAtUtc,
        string contentDigest)
    {
        PromotionDomainRules.RequireIdentifier(id, nameof(id));
        PromotionDomainRules.RequireIdentifier(productId, nameof(productId));
        ArgumentOutOfRangeException.ThrowIfLessThan(revisionNumber, 1);
        ArgumentNullException.ThrowIfNull(displayNames);
        ArgumentNullException.ThrowIfNull(presentationFeatures);
        PromotionDomainRules.RequireIdentifier(createdByActorId, nameof(createdByActorId));
        PromotionDomainRules.RequireUtc(createdAtUtc, nameof(createdAtUtc));

        var normalizedNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (locale, name) in displayNames)
        {
            var normalizedLocale = PromotionDomainRules.RequireLocale(locale, nameof(displayNames));
            var normalizedName = PromotionDomainRules.RequireText(name, nameof(displayNames), 200);
            if (!normalizedNames.TryAdd(normalizedLocale, normalizedName))
            {
                throw new PromotionDomainException(
                    "PROMOTION_PRODUCT_LOCALE_DUPLICATE",
                    $"Promotion product revision contains locale '{normalizedLocale}' more than once.");
            }
        }

        if (normalizedNames.Count == 0)
        {
            throw new PromotionDomainException(
                "PROMOTION_PRODUCT_NAME_REQUIRED",
                "Promotion product revision requires at least one localized display name.");
        }

        var featureSet = presentationFeatures.ToHashSet();
        if (featureSet.Count == 0 || featureSet.Any(feature => !Enum.IsDefined(feature)))
        {
            throw new PromotionDomainException(
                "PROMOTION_PRODUCT_FEATURES_INVALID",
                "Promotion product revision requires one or more supported presentation features.");
        }

        var normalizedCapability = string.IsNullOrWhiteSpace(requiredContactCapability)
            ? null
            : PromotionDomainRules.RequireKey(requiredContactCapability, nameof(requiredContactCapability));
        if (normalizedCapability is not null && !requiresVerifiedContact)
        {
            throw new PromotionDomainException(
                "PROMOTION_PRODUCT_CONTACT_REQUIREMENT_INVALID",
                "A required contact capability can be declared only when verified contact is required.");
        }

        return new PromotionProductRevision(
            id,
            productId,
            revisionNumber,
            new ReadOnlyDictionary<string, string>(normalizedNames),
            new ReadOnlySet<PromotionPresentationFeature>(featureSet),
            requiresVerifiedContact,
            normalizedCapability,
            createdByActorId,
            createdAtUtc,
            PromotionDomainRules.RequireDigest(contentDigest, nameof(contentDigest)));
    }
}

/// <summary>Stable Promotion product identity with one exact active immutable revision.</summary>
public sealed class PromotionProduct
{
    private PromotionProduct(
        Guid id,
        string key,
        PromotionProductState state,
        PromotionProductRevision currentRevision,
        long aggregateRevision)
    {
        Id = id;
        Key = key;
        State = state;
        CurrentRevision = currentRevision;
        AggregateRevision = aggregateRevision;
    }

    public Guid Id { get; }

    public string Key { get; }

    public PromotionProductState State { get; private set; }

    public PromotionProductRevision CurrentRevision { get; private set; }

    public long AggregateRevision { get; private set; }

    public static PromotionProduct Create(
        Guid id,
        string key,
        Guid revisionId,
        IReadOnlyDictionary<string, string> displayNames,
        IEnumerable<PromotionPresentationFeature> presentationFeatures,
        bool requiresVerifiedContact,
        string? requiredContactCapability,
        Guid actorId,
        DateTimeOffset createdAtUtc,
        string contentDigest)
    {
        PromotionDomainRules.RequireIdentifier(id, nameof(id));
        var normalizedKey = PromotionDomainRules.RequireKey(key, nameof(key));
        var revision = PromotionProductRevision.Create(
            revisionId,
            id,
            1,
            displayNames,
            presentationFeatures,
            requiresVerifiedContact,
            requiredContactCapability,
            actorId,
            createdAtUtc,
            contentDigest);
        return new PromotionProduct(id, normalizedKey, PromotionProductState.Active, revision, 1);
    }

    public PromotionProductRevision AddRevision(
        long expectedAggregateRevision,
        Guid revisionId,
        IReadOnlyDictionary<string, string> displayNames,
        IEnumerable<PromotionPresentationFeature> presentationFeatures,
        bool requiresVerifiedContact,
        string? requiredContactCapability,
        Guid actorId,
        DateTimeOffset createdAtUtc,
        string contentDigest)
    {
        EnsureMutable(expectedAggregateRevision);
        var revision = PromotionProductRevision.Create(
            revisionId,
            Id,
            CurrentRevision.RevisionNumber + 1,
            displayNames,
            presentationFeatures,
            requiresVerifiedContact,
            requiredContactCapability,
            actorId,
            createdAtUtc,
            contentDigest);
        CurrentRevision = revision;
        AggregateRevision++;
        return revision;
    }

    public void ChangeState(
        long expectedAggregateRevision,
        PromotionProductState state)
    {
        PromotionDomainRules.RequireExpectedRevision(
            AggregateRevision,
            expectedAggregateRevision,
            "Promotion product");
        if (!Enum.IsDefined(state))
        {
            throw new PromotionDomainException(
                "PROMOTION_PRODUCT_STATE_INVALID",
                $"Promotion product state '{state}' is unsupported.");
        }

        if (State == PromotionProductState.Archived)
        {
            throw new PromotionDomainException(
                "PROMOTION_PRODUCT_ARCHIVED",
                "An archived Promotion product cannot change state.");
        }

        if (State == state)
        {
            throw new PromotionDomainException(
                "PROMOTION_PRODUCT_STATE_UNCHANGED",
                "Promotion product state command must change the current state.");
        }

        State = state;
        AggregateRevision++;
    }

    public static PromotionProduct Restore(
        Guid id,
        string key,
        PromotionProductState state,
        PromotionProductRevision currentRevision,
        long aggregateRevision)
    {
        PromotionDomainRules.RequireIdentifier(id, nameof(id));
        var normalizedKey = PromotionDomainRules.RequireKey(key, nameof(key));
        ArgumentNullException.ThrowIfNull(currentRevision);
        if (currentRevision.ProductId != id)
        {
            throw new PromotionDomainException(
                "PROMOTION_PRODUCT_REVISION_OWNER_MISMATCH",
                "Promotion product revision belongs to another product identity.");
        }

        if (!Enum.IsDefined(state))
        {
            throw new PromotionDomainException(
                "PROMOTION_PRODUCT_STATE_INVALID",
                $"Promotion product state '{state}' is unsupported.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(aggregateRevision, 1);
        if (aggregateRevision < currentRevision.RevisionNumber)
        {
            throw new PromotionDomainException(
                "PROMOTION_PRODUCT_REVISION_INVALID",
                "Promotion product aggregate revision cannot trail its current semantic revision.");
        }

        return new PromotionProduct(id, normalizedKey, state, currentRevision, aggregateRevision);
    }

    private void EnsureMutable(long expectedAggregateRevision)
    {
        PromotionDomainRules.RequireExpectedRevision(
            AggregateRevision,
            expectedAggregateRevision,
            "Promotion product");
        if (State == PromotionProductState.Archived)
        {
            throw new PromotionDomainException(
                "PROMOTION_PRODUCT_ARCHIVED",
                "An archived Promotion product cannot receive another revision.");
        }
    }
}
