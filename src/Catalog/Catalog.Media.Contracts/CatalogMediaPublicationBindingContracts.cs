namespace Aggregator.Catalog.Media.Contracts;

public enum CatalogMediaPublicationRightsBasisContract
{
    OwnerProvided = 1,
    ExplicitLicense = 2,
    PublicDomain = 3,
}

/// <summary>
/// Producer-owned immutable Catalog Media output for one exact accepted asset revision and public variant.
/// </summary>
public sealed record CatalogMediaPublicationBindingContract(
    Guid MediaId,
    long MediaAggregateRevision,
    Guid VariantId,
    string CatalogKey,
    string ObjectUri,
    string ContentType,
    string ContentDigest,
    CatalogMediaPublicationRightsBasisContract RightsBasis);

public interface ICatalogMediaPublicationBindingAuthority
{
    public Task<CatalogMediaPublicationBindingContract> RequirePublishableBindingAsync(
        string catalogKey,
        Guid mediaId,
        long expectedMediaAggregateRevision,
        Guid variantId,
        CancellationToken cancellationToken);
}
