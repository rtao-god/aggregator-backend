using System.Collections.ObjectModel;

namespace Aggregator.Promotion.Domain;

/// <summary>Typed owner verdict for one listing/product/scope eligibility check.</summary>
public enum ListingPromotionEligibilityStatus
{
    Eligible = 1,
    ProductInactive = 2,
    ListingUnavailable = 3,
    VerifiedContactRequired = 4,
    ContactCapabilityRequired = 5,
    ScopeMismatch = 6,
}

/// <summary>Promotion-owned result used by commands and automatic placement reconciliation.</summary>
public sealed record ListingPromotionEligibilityDecision(
    ListingPromotionEligibilityStatus Status,
    string Code,
    string Detail)
{
    public bool IsEligible => Status == ListingPromotionEligibilityStatus.Eligible;
}

/// <summary>Promotion-local fail-closed projection of Catalog listing eligibility.</summary>
public sealed record ListingPromotionEligibility
{
    private ListingPromotionEligibility(
        string catalogKey,
        Guid listingId,
        bool isPublished,
        bool isArchived,
        bool hasBlockingDispute,
        bool hasVerifiedContact,
        IReadOnlySet<string> contactCapabilities,
        IReadOnlySet<string> categoryKeys,
        string? districtKey,
        long sourceRevision,
        DateTimeOffset changedAtUtc)
    {
        CatalogKey = catalogKey;
        ListingId = listingId;
        IsPublished = isPublished;
        IsArchived = isArchived;
        HasBlockingDispute = hasBlockingDispute;
        HasVerifiedContact = hasVerifiedContact;
        ContactCapabilities = contactCapabilities;
        CategoryKeys = categoryKeys;
        DistrictKey = districtKey;
        SourceRevision = sourceRevision;
        ChangedAtUtc = changedAtUtc;
    }

    public string CatalogKey { get; }

    public Guid ListingId { get; }

    public bool IsPublished { get; }

    public bool IsArchived { get; }

    public bool HasBlockingDispute { get; }

    public bool HasVerifiedContact { get; }

    public IReadOnlySet<string> ContactCapabilities { get; }

    public IReadOnlySet<string> CategoryKeys { get; }

    public string? DistrictKey { get; }

    public long SourceRevision { get; }

    public DateTimeOffset ChangedAtUtc { get; }

    public static ListingPromotionEligibility Create(
        string catalogKey,
        Guid listingId,
        bool isPublished,
        bool isArchived,
        bool hasBlockingDispute,
        bool hasVerifiedContact,
        IEnumerable<string> contactCapabilities,
        IEnumerable<string> categoryKeys,
        string? districtKey,
        long sourceRevision,
        DateTimeOffset changedAtUtc)
    {
        var normalizedCatalogKey = PromotionDomainRules.RequireKey(catalogKey, nameof(catalogKey));
        PromotionDomainRules.RequireIdentifier(listingId, nameof(listingId));
        ArgumentNullException.ThrowIfNull(contactCapabilities);
        ArgumentNullException.ThrowIfNull(categoryKeys);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRevision, 1);
        PromotionDomainRules.RequireUtc(changedAtUtc, nameof(changedAtUtc));

        var normalizedCapabilities = contactCapabilities
            .Select(value => PromotionDomainRules.RequireKey(value, nameof(contactCapabilities)))
            .ToHashSet(StringComparer.Ordinal);
        var normalizedCategories = categoryKeys
            .Select(value => PromotionDomainRules.RequireKey(value, nameof(categoryKeys)))
            .ToHashSet(StringComparer.Ordinal);
        var normalizedDistrict = string.IsNullOrWhiteSpace(districtKey)
            ? null
            : PromotionDomainRules.RequireKey(districtKey, nameof(districtKey));
        if (isArchived && isPublished)
        {
            throw new PromotionDomainException(
                "PROMOTION_ELIGIBILITY_STATE_INVALID",
                "An archived listing cannot be projected as published and Promotion-eligible.");
        }

        if (!hasVerifiedContact && normalizedCapabilities.Count > 0)
        {
            throw new PromotionDomainException(
                "PROMOTION_ELIGIBILITY_CONTACT_INVALID",
                "Verified contact capabilities cannot be projected when verified contact is absent.");
        }

        return new ListingPromotionEligibility(
            normalizedCatalogKey,
            listingId,
            isPublished,
            isArchived,
            hasBlockingDispute,
            hasVerifiedContact,
            new ReadOnlySet<string>(normalizedCapabilities),
            new ReadOnlySet<string>(normalizedCategories),
            normalizedDistrict,
            sourceRevision,
            changedAtUtc);
    }

    public ListingPromotionEligibilityDecision Evaluate(
        PromotionProduct product,
        PlacementScopeType scopeType,
        string scopeKey)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (product.State != PromotionProductState.Active)
        {
            return new ListingPromotionEligibilityDecision(
                ListingPromotionEligibilityStatus.ProductInactive,
                "PROMOTION_PRODUCT_NOT_ACTIVE",
                $"Promotion product '{product.Key}' is not active.");
        }

        if (!IsPublished || IsArchived || HasBlockingDispute)
        {
            return new ListingPromotionEligibilityDecision(
                ListingPromotionEligibilityStatus.ListingUnavailable,
                "PROMOTION_LISTING_INELIGIBLE",
                $"Listing '{ListingId}' is not eligible for Promotion in its current Catalog state.");
        }

        if (product.CurrentRevision.RequiresVerifiedContact && !HasVerifiedContact)
        {
            return new ListingPromotionEligibilityDecision(
                ListingPromotionEligibilityStatus.VerifiedContactRequired,
                "PROMOTION_VERIFIED_CONTACT_REQUIRED",
                $"Promotion product '{product.Key}' requires a verified listing contact.");
        }

        if (product.CurrentRevision.RequiredContactCapability is { } requiredCapability &&
            !ContactCapabilities.Contains(requiredCapability))
        {
            return new ListingPromotionEligibilityDecision(
                ListingPromotionEligibilityStatus.ContactCapabilityRequired,
                "PROMOTION_CONTACT_CAPABILITY_REQUIRED",
                $"Promotion product '{product.Key}' requires contact capability '{requiredCapability}'.");
        }

        var normalizedScopeKey = PromotionDomainRules.RequireKey(scopeKey, nameof(scopeKey));
        var scopeMatches = scopeType switch
        {
            PlacementScopeType.Catalog => string.Equals(
                normalizedScopeKey,
                CatalogKey,
                StringComparison.Ordinal),
            PlacementScopeType.Category => CategoryKeys.Contains(normalizedScopeKey),
            PlacementScopeType.District => string.Equals(
                normalizedScopeKey,
                DistrictKey,
                StringComparison.Ordinal),
            PlacementScopeType.EditorialLanding => true,
            _ => throw new PromotionDomainException(
                "PROMOTION_SCOPE_TYPE_INVALID",
                $"Placement scope type '{scopeType}' is unsupported."),
        };
        return scopeMatches
            ? new ListingPromotionEligibilityDecision(
                ListingPromotionEligibilityStatus.Eligible,
                "PROMOTION_LISTING_ELIGIBLE",
                $"Listing '{ListingId}' is eligible for Promotion scope '{scopeType}:{normalizedScopeKey}'.")
            : new ListingPromotionEligibilityDecision(
                ListingPromotionEligibilityStatus.ScopeMismatch,
                "PROMOTION_LISTING_SCOPE_MISMATCH",
                $"Listing '{ListingId}' is not eligible for scope '{scopeType}:{normalizedScopeKey}'.");
    }

    public void EnsureEligible(
        PromotionProduct product,
        PlacementScopeType scopeType,
        string scopeKey)
    {
        var decision = Evaluate(product, scopeType, scopeKey);
        if (!decision.IsEligible)
        {
            throw new PromotionDomainException(decision.Code, decision.Detail);
        }
    }
}
