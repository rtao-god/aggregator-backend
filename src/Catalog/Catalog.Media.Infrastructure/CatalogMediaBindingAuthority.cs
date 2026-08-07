using Aggregator.Catalog.Application;
using Aggregator.Catalog.Domain;
using Aggregator.Catalog.Media.Application;
using Aggregator.Catalog.Media.Domain;

namespace Aggregator.Catalog.Media.Infrastructure;

public sealed class CatalogMediaBindingAuthority(ICatalogMediaRepository repository)
    : ICatalogMediaBindingAuthority
{
    public async Task<CatalogMediaPublicationBinding> RequirePublishableBindingAsync(
        CatalogKey catalogKey, Guid mediaId, long expectedMediaAggregateRevision, Guid variantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogKey);
        if (mediaId == Guid.Empty)
            throw new CatalogContractException("catalog.media_asset_required", "Catalog media binding requires an exact asset ID.");
        if (variantId == Guid.Empty)
            throw new CatalogContractException("catalog.media_variant_required", "Catalog media binding requires an exact variant ID.");
        if (expectedMediaAggregateRevision <= 0)
            throw new CatalogContractException("catalog.media_revision_invalid", "Catalog media binding requires a positive expected aggregate revision.");
        var asset = await repository.GetAsync(mediaId, cancellationToken)
            ?? throw new CatalogNotFoundException("catalog-media-asset", mediaId);
        if (!string.Equals(asset.CatalogKey, catalogKey.Value, StringComparison.Ordinal))
            throw new CatalogConflictException($"Catalog media asset '{mediaId}' belongs to catalog '{asset.CatalogKey}', not '{catalogKey}'.");
        if (asset.AggregateRevision != expectedMediaAggregateRevision)
            throw new CatalogConflictException($"Catalog media asset '{mediaId}' expected revision '{expectedMediaAggregateRevision}', actual '{asset.AggregateRevision}'.");
        if (asset.State != CatalogMediaState.Accepted || asset.AcceptedAtUtc is null || asset.RightsRevokedAtUtc is not null)
            throw new CatalogConflictException($"Catalog media asset '{mediaId}' is in state '{asset.State}' and is not publishable.");
        var variant = asset.Variants.SingleOrDefault(candidate => candidate.Id == variantId)
            ?? throw new CatalogNotFoundException("catalog-media-variant", variantId);
        if (variant.AssetId != asset.Id || !variant.ObjectKey.StartsWith("catalog-media/published/", StringComparison.Ordinal))
            throw new CatalogInvariantException($"Catalog media variant '{variantId}' has an invalid owner identity.");
        return new CatalogMediaPublicationBinding(
            asset.Id, asset.AggregateRevision, variant.Id, CreateObjectUri(asset.Id, variant.Id, variant.ContentDigest),
            variant.ContentType, variant.ContentDigest, MapRightsBasis(asset.RightsBasis));
    }

    private static Uri CreateObjectUri(Guid mediaId, Guid variantId, string contentDigest) =>
        new($"urn:aggregator:catalog-media:{mediaId:N}:{variantId:N}:{contentDigest}", UriKind.Absolute);

    private static MediaRightsBasis MapRightsBasis(CatalogMediaRightsBasis rightsBasis) => rightsBasis switch
    {
        CatalogMediaRightsBasis.OwnerProvided => MediaRightsBasis.OwnerProvided,
        CatalogMediaRightsBasis.Licensed => MediaRightsBasis.ExplicitLicense,
        CatalogMediaRightsBasis.PublicDomain => MediaRightsBasis.PublicDomain,
        _ => throw new CatalogInvariantException($"Catalog media rights basis '{rightsBasis}' cannot be projected into a listing binding."),
    };
}
