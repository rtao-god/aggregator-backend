using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Domain;

namespace Aggregator.Promotion.Application;

public static class PromotionContractMapper
{
    public static PromotionProductResponse ToResponse(PromotionProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        return new PromotionProductResponse(
            product.Id,
            product.Key,
            ToContract(product.State),
            ToResponse(product.CurrentRevision),
            product.AggregateRevision);
    }

    public static PromotionEntitlementResponse ToResponse(PromotionEntitlement entitlement)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        return new PromotionEntitlementResponse(
            entitlement.Id,
            entitlement.ListingId,
            entitlement.ProductKey,
            ToContract(entitlement.SourceType),
            entitlement.ExternalReference,
            entitlement.EffectiveWindow.StartsAtUtc,
            entitlement.EffectiveWindow.EndsAtUtc,
            ToContract(entitlement.State),
            entitlement.CreatedByActorId,
            entitlement.AuditReason,
            entitlement.CreatedAtUtc,
            entitlement.ChangedAtUtc,
            entitlement.AggregateRevision);
    }

    public static SponsoredPlacementResponse ToResponse(SponsoredPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return new SponsoredPlacementResponse(
            placement.Id,
            placement.EntitlementId,
            placement.ListingId,
            placement.ProductKey,
            ToContract(placement.State),
            ToResponse(placement.CurrentRevision),
            placement.HardExpiryAtUtc,
            placement.ChangedAtUtc,
            placement.AuditReason,
            placement.AggregateRevision);
    }

    public static PromotionEntitlementChanged ToEvent(
        PromotionEntitlement entitlement,
        Guid eventId,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        return new PromotionEntitlementChanged(
            eventId,
            entitlement.Id,
            entitlement.ListingId,
            entitlement.ProductKey,
            ToContract(entitlement.State),
            entitlement.EffectiveWindow.StartsAtUtc,
            entitlement.EffectiveWindow.EndsAtUtc,
            entitlement.AggregateRevision,
            occurredAtUtc);
    }

    public static SponsoredPlacementChanged ToEvent(
        SponsoredPlacement placement,
        Guid eventId,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var revision = placement.CurrentRevision;
        return new SponsoredPlacementChanged(
            eventId,
            placement.Id,
            placement.EntitlementId,
            placement.ListingId,
            revision.CatalogKey,
            placement.ProductKey,
            ToContract(revision.ScopeType),
            revision.ScopeKey,
            revision.LocaleScope.Order(StringComparer.Ordinal).ToArray(),
            revision.EffectiveWindow.StartsAtUtc,
            revision.EffectiveWindow.EndsAtUtc,
            placement.HardExpiryAtUtc,
            revision.PriorityBand,
            revision.CapacitySlot,
            revision.PresentationLabelKey,
            ToContract(placement.State),
            placement.AggregateRevision,
            occurredAtUtc);
    }

    public static PromotionPresentationFeature ToDomain(
        PromotionPresentationFeatureContract feature) => feature switch
        {
            PromotionPresentationFeatureContract.FeaturedListing => PromotionPresentationFeature.FeaturedListing,
            PromotionPresentationFeatureContract.SponsoredSlot => PromotionPresentationFeature.SponsoredSlot,
            PromotionPresentationFeatureContract.ExtendedCard => PromotionPresentationFeature.ExtendedCard,
            PromotionPresentationFeatureContract.ExtendedGallery => PromotionPresentationFeature.ExtendedGallery,
            _ => throw Unsupported(nameof(feature), feature),
        };

    public static PromotionProductState ToDomain(PromotionProductStateContract state) => state switch
        {
            PromotionProductStateContract.Active => PromotionProductState.Active,
            PromotionProductStateContract.Inactive => PromotionProductState.Inactive,
            PromotionProductStateContract.Archived => PromotionProductState.Archived,
            _ => throw Unsupported(nameof(state), state),
        };

    public static PromotionEntitlementSourceType ToDomain(
        PromotionEntitlementSourceTypeContract sourceType) => sourceType switch
        {
            PromotionEntitlementSourceTypeContract.ManualContract => PromotionEntitlementSourceType.ManualContract,
            PromotionEntitlementSourceTypeContract.ManualTrial => PromotionEntitlementSourceType.ManualTrial,
            PromotionEntitlementSourceTypeContract.AdministrativeGrant => PromotionEntitlementSourceType.AdministrativeGrant,
            _ => throw Unsupported(nameof(sourceType), sourceType),
        };

    public static PlacementScopeType ToDomain(PlacementScopeTypeContract scopeType) => scopeType switch
        {
            PlacementScopeTypeContract.Catalog => PlacementScopeType.Catalog,
            PlacementScopeTypeContract.Category => PlacementScopeType.Category,
            PlacementScopeTypeContract.District => PlacementScopeType.District,
            PlacementScopeTypeContract.EditorialLanding => PlacementScopeType.EditorialLanding,
            _ => throw Unsupported(nameof(scopeType), scopeType),
        };

    private static PromotionProductRevisionResponse ToResponse(PromotionProductRevision revision) =>
        new(
            revision.Id,
            revision.ProductId,
            revision.RevisionNumber,
            revision.DisplayNames,
            revision.PresentationFeatures
                .OrderBy(feature => (int)feature)
                .Select(ToContract)
                .ToArray(),
            revision.RequiresVerifiedContact,
            revision.RequiredContactCapability,
            revision.CreatedByActorId,
            revision.CreatedAtUtc,
            revision.ContentDigest);

    private static SponsoredPlacementRevisionResponse ToResponse(SponsoredPlacementRevision revision) =>
        new(
            revision.Id,
            revision.PlacementId,
            revision.RevisionNumber,
            revision.CatalogKey,
            ToContract(revision.ScopeType),
            revision.ScopeKey,
            revision.LocaleScope.Order(StringComparer.Ordinal).ToArray(),
            revision.EffectiveWindow.StartsAtUtc,
            revision.EffectiveWindow.EndsAtUtc,
            revision.PriorityBand,
            revision.CapacitySlot,
            revision.PresentationLabelKey,
            revision.CreatedByActorId,
            revision.CreatedAtUtc,
            revision.ContentDigest);

    private static PromotionProductStateContract ToContract(PromotionProductState state) => state switch
        {
            PromotionProductState.Active => PromotionProductStateContract.Active,
            PromotionProductState.Inactive => PromotionProductStateContract.Inactive,
            PromotionProductState.Archived => PromotionProductStateContract.Archived,
            _ => throw Unsupported(nameof(state), state),
        };

    private static PromotionPresentationFeatureContract ToContract(
        PromotionPresentationFeature feature) => feature switch
        {
            PromotionPresentationFeature.FeaturedListing => PromotionPresentationFeatureContract.FeaturedListing,
            PromotionPresentationFeature.SponsoredSlot => PromotionPresentationFeatureContract.SponsoredSlot,
            PromotionPresentationFeature.ExtendedCard => PromotionPresentationFeatureContract.ExtendedCard,
            PromotionPresentationFeature.ExtendedGallery => PromotionPresentationFeatureContract.ExtendedGallery,
            _ => throw Unsupported(nameof(feature), feature),
        };

    private static PromotionEntitlementSourceTypeContract ToContract(
        PromotionEntitlementSourceType sourceType) => sourceType switch
        {
            PromotionEntitlementSourceType.ManualContract => PromotionEntitlementSourceTypeContract.ManualContract,
            PromotionEntitlementSourceType.ManualTrial => PromotionEntitlementSourceTypeContract.ManualTrial,
            PromotionEntitlementSourceType.AdministrativeGrant => PromotionEntitlementSourceTypeContract.AdministrativeGrant,
            _ => throw Unsupported(nameof(sourceType), sourceType),
        };

    private static PromotionEntitlementStateContract ToContract(
        PromotionEntitlementState state) => state switch
        {
            PromotionEntitlementState.Scheduled => PromotionEntitlementStateContract.Scheduled,
            PromotionEntitlementState.Active => PromotionEntitlementStateContract.Active,
            PromotionEntitlementState.Paused => PromotionEntitlementStateContract.Paused,
            PromotionEntitlementState.Revoked => PromotionEntitlementStateContract.Revoked,
            PromotionEntitlementState.Expired => PromotionEntitlementStateContract.Expired,
            _ => throw Unsupported(nameof(state), state),
        };

    private static PlacementScopeTypeContract ToContract(PlacementScopeType scopeType) => scopeType switch
        {
            PlacementScopeType.Catalog => PlacementScopeTypeContract.Catalog,
            PlacementScopeType.Category => PlacementScopeTypeContract.Category,
            PlacementScopeType.District => PlacementScopeTypeContract.District,
            PlacementScopeType.EditorialLanding => PlacementScopeTypeContract.EditorialLanding,
            _ => throw Unsupported(nameof(scopeType), scopeType),
        };

    private static SponsoredPlacementStateContract ToContract(
        SponsoredPlacementState state) => state switch
        {
            SponsoredPlacementState.Scheduled => SponsoredPlacementStateContract.Scheduled,
            SponsoredPlacementState.Active => SponsoredPlacementStateContract.Active,
            SponsoredPlacementState.Paused => SponsoredPlacementStateContract.Paused,
            SponsoredPlacementState.Ended => SponsoredPlacementStateContract.Ended,
            SponsoredPlacementState.Revoked => SponsoredPlacementStateContract.Revoked,
            _ => throw Unsupported(nameof(state), state),
        };

    private static PromotionApplicationException Unsupported<TEnum>(string field, TEnum value)
        where TEnum : struct, Enum =>
        new(
            "Promotion.Contracts",
            "PROMOTION_ENUM_UNSUPPORTED",
            422,
            $"Promotion field '{field}' contains unsupported value '{value}'.",
            "Use one enum token declared by the active Promotion contract.");
}
