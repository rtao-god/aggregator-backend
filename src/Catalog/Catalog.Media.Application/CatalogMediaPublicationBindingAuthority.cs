using Aggregator.Catalog.Media.Domain;

namespace Aggregator.Catalog.Media.Application;

public enum CatalogMediaPublicationRightsBasis
{
    OwnerProvided = 1,
    ExplicitLicense = 2,
    PublicDomain = 3,
}

/// <summary>
/// Immutable Catalog Media owner output used when a listing revision binds one exact public variant.
/// </summary>
public sealed record CatalogMediaPublicationBinding(
    Guid MediaId,
    long MediaAggregateRevision,
    Guid VariantId,
    string CatalogKey,
    Uri ObjectUri,
    string ContentType,
    string ContentDigest,
    CatalogMediaPublicationRightsBasis RightsBasis);

/// <summary>
/// Resolves one exact Catalog Media asset revision and public variant before Catalog records a listing binding.
/// </summary>
public interface ICatalogMediaPublicationBindingAuthority
{
    Task<CatalogMediaPublicationBinding> RequirePublishableBindingAsync(
        string catalogKey,
        Guid mediaId,
        long expectedMediaAggregateRevision,
        Guid variantId,
        CancellationToken cancellationToken);
}

public sealed class CatalogMediaPublicationBindingAuthority(ICatalogMediaRepository repository)
    : ICatalogMediaPublicationBindingAuthority
{
    public async Task<CatalogMediaPublicationBinding> RequirePublishableBindingAsync(
        string catalogKey,
        Guid mediaId,
        long expectedMediaAggregateRevision,
        Guid variantId,
        CancellationToken cancellationToken)
    {
        var normalizedCatalogKey = RequireCatalogKey(catalogKey);
        if (mediaId == Guid.Empty)
        {
            throw Failure(
                "CATALOG_MEDIA_BINDING_ASSET_REQUIRED",
                400,
                "Catalog media binding requires an exact asset ID.",
                "Use the media asset ID returned by Catalog Media registration.");
        }

        if (variantId == Guid.Empty)
        {
            throw Failure(
                "CATALOG_MEDIA_BINDING_VARIANT_REQUIRED",
                400,
                "Catalog media binding requires an exact variant ID.",
                "Use a public variant ID emitted by Catalog Media processing.");
        }

        if (expectedMediaAggregateRevision <= 0)
        {
            throw Failure(
                "CATALOG_MEDIA_BINDING_REVISION_INVALID",
                400,
                "Catalog media binding requires a positive expected aggregate revision.",
                "Reload the exact media asset and submit its current aggregate revision.");
        }

        var asset = await repository.GetAsync(mediaId, cancellationToken)
            ?? throw Failure(
                "CATALOG_MEDIA_BINDING_ASSET_NOT_FOUND",
                404,
                $"Catalog media asset '{mediaId}' was not found.",
                "Reload the exact media asset before creating a listing revision.");

        if (!string.Equals(asset.CatalogKey, normalizedCatalogKey, StringComparison.Ordinal))
        {
            throw Failure(
                "CATALOG_MEDIA_BINDING_CATALOG_MISMATCH",
                409,
                $"Catalog media asset '{mediaId}' belongs to catalog '{asset.CatalogKey}', not '{normalizedCatalogKey}'.",
                "Use an asset owned by the listing catalog.");
        }

        if (asset.AggregateRevision != expectedMediaAggregateRevision)
        {
            throw Failure(
                "CATALOG_MEDIA_BINDING_REVISION_CONFLICT",
                409,
                $"Catalog media asset '{mediaId}' expected revision '{expectedMediaAggregateRevision}', actual '{asset.AggregateRevision}'.",
                "Reload the media asset and submit its exact current aggregate revision.");
        }

        if (asset.State != CatalogMediaState.Accepted ||
            asset.AcceptedAtUtc is null ||
            asset.RightsRevokedAtUtc is not null)
        {
            throw Failure(
                "CATALOG_MEDIA_BINDING_NOT_PUBLISHABLE",
                422,
                $"Catalog media asset '{mediaId}' is in state '{asset.State}' and cannot be bound to a listing revision.",
                "Complete scanning and approval, or replace an asset whose rights were revoked.");
        }

        var variant = asset.Variants.SingleOrDefault(candidate => candidate.Id == variantId)
            ?? throw Failure(
                "CATALOG_MEDIA_BINDING_VARIANT_NOT_FOUND",
                404,
                $"Catalog media variant '{variantId}' does not belong to asset '{mediaId}'.",
                "Use an exact variant emitted for the selected media asset revision.");

        if (variant.AssetId != asset.Id ||
            !variant.ObjectKey.StartsWith("catalog-media/published/", StringComparison.Ordinal))
        {
            throw Failure(
                "CATALOG_MEDIA_BINDING_VARIANT_IDENTITY_INVALID",
                500,
                $"Catalog media variant '{variantId}' has an invalid owner identity.",
                "Restore the media aggregate from a verified Catalog database backup.");
        }

        return new CatalogMediaPublicationBinding(
            asset.Id,
            asset.AggregateRevision,
            variant.Id,
            asset.CatalogKey,
            CreateObjectUri(asset.Id, variant.Id, variant.ContentDigest),
            variant.ContentType,
            variant.ContentDigest,
            MapRightsBasis(asset.RightsBasis));
    }

    private static CatalogMediaPublicationRightsBasis MapRightsBasis(
        CatalogMediaRightsBasis rightsBasis) => rightsBasis switch
    {
        CatalogMediaRightsBasis.OwnerProvided => CatalogMediaPublicationRightsBasis.OwnerProvided,
        CatalogMediaRightsBasis.Licensed => CatalogMediaPublicationRightsBasis.ExplicitLicense,
        CatalogMediaRightsBasis.PublicDomain => CatalogMediaPublicationRightsBasis.PublicDomain,
        _ => throw Failure(
            "CATALOG_MEDIA_BINDING_RIGHTS_BASIS_INVALID",
            500,
            $"Catalog media rights basis '{rightsBasis}' cannot be projected into a listing binding.",
            "Correct the Catalog Media rights-basis owner before retrying."),
    };

    private static Uri CreateObjectUri(Guid mediaId, Guid variantId, string contentDigest) =>
        new(
            $"urn:aggregator:catalog-media:{mediaId:N}:{variantId:N}:{contentDigest}",
            UriKind.Absolute);

    private static string RequireCatalogKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 120 || normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("Catalog key contains unsupported characters.", nameof(value));
        }

        return normalized;
    }

    private static CatalogMediaApplicationException Failure(
        string code,
        int status,
        string message,
        string requiredAction) =>
        new(
            "Catalog.Media.Bindings",
            code,
            status,
            message,
            requiredAction);
}
