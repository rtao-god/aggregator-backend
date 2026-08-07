using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>Exact Catalog Media owner output that may be sealed into one listing revision.</summary>
public sealed record CatalogMediaPublicationBinding(
    Guid MediaId,
    long MediaAggregateRevision,
    Guid VariantId,
    Uri ObjectUri,
    string ContentType,
    string ContentDigest,
    MediaRightsBasis RightsBasis);

/// <summary>Validates and resolves one exact publishable Catalog Media asset revision and variant.</summary>
public interface ICatalogMediaBindingAuthority
{
    Task<CatalogMediaPublicationBinding> RequirePublishableBindingAsync(
        CatalogKey catalogKey,
        Guid mediaId,
        long expectedMediaAggregateRevision,
        Guid variantId,
        CancellationToken cancellationToken);
}
