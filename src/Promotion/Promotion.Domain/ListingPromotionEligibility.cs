using System.Collections.ObjectModel;

namespace Aggregator.Promotion.Domain;

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

    public void EnsureEligible(
        PromotionProduct product,
        PlacementScopeType scopeType,
        string scopeKey)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (product.State != PromotionProductState.Active)
        {
            throw new PromotionDomainException(
                "PROMOTION_PRODUCT_NOT_ACTIVE",
                $"Promotion product '{product.Key}' is not active.");
        }

        if (!IsPublished || IsArchived || HasBlockingDispute)
        {
            throw new PromotionDomainException(
                "PROMOTION_LISTING_INELIGIBLE",
                $"Listing '{ListingId}' is not eligible for Promotion in its current Catalog state.");
        }

        if (product.CurrentRevision.RequiresVerifiedContact && !HasVerifiedContact)
        {
            throw new PromotionDomainException(
                "PROMOTION_VERIFIED_CONTACT_REQUIRED",
                $"Promotion product '{product.Key}' requires a verified listing contact.");
        }

        if (product.CurrentRevision.RequiredContactCapability is { } requiredCapability &&
            !ContactCapabilities.Contains(requiredCapability))
        {
            throw new PromotionDomainException(
                "PROMOTION_CONTACT_CAPABILITY_REQUIRED",
                $"Promotion product '{product.Key}' requires contact capability '{requiredCapability}'.");
        }

        var normalizedScopeKey = PromotionDomainRules.RequireKey(scopeKey, nameof(scopeKey));
        switch (scopeType)
        {
            case PlacementScopeType.Catalog when !string.Equals(
                normalizedScopeKey,
                CatalogKey,
                StringComparison.Ordinal):
                throw ScopeMismatch(scopeType, normalizedScopeKey);
            case PlacementScopeType.Category when !CategoryKeys.Contains(normalizedScopeKey):
                throw ScopeMismatch(scopeType, normalizedScopeKey);
            case PlacementScopeType.District when !string.Equals(
                normalizedScopeKey,
                DistrictKey,
                StringComparison.Ordinal):
                throw ScopeMismatch(scopeType, normalizedScopeKey);
            case PlacementScopeType.EditorialLanding:
                break;
            case PlacementScopeType.Catalog or PlacementScopeType.Category or PlacementScopeType.District:
                break;
            default:
                throw new PromotionDomainException(
                    "PROMOTION_SCOPE_TYPE_INVALID",
                    $"Placement scope type '{scopeType}' is unsupported.");
        }
    }

    private PromotionDomainException ScopeMismatch(PlacementScopeType scopeType, string scopeKey) =>
        new(
            "PROMOTION_LISTING_SCOPE_MISMATCH",
            $"Listing '{ListingId}' is not eligible for scope '{scopeType}:{scopeKey}'.");
}
