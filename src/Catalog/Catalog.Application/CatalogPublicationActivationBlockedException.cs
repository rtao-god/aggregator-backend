namespace Aggregator.Catalog.Application;

public enum CatalogPublicationActivationBlockReason
{
    PointerIdentityMismatch = 1,
    MediaNotPublishable = 2,
    PublicVisibilitySuppression = 3,
    ListingDispute = 4,
}

/// <summary>Typed owner failure raised when the Catalog database activation gate rejects a publication.</summary>
public sealed class CatalogPublicationActivationBlockedException : InvalidOperationException
{
    public CatalogPublicationActivationBlockedException(
        string catalogKey,
        Guid publicationId,
        CatalogPublicationActivationBlockReason reason,
        string requiredAction,
        string detail)
        : base(detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredAction);
        CatalogKey = catalogKey;
        PublicationId = publicationId;
        Reason = reason;
        RequiredAction = requiredAction;
    }

    public string CatalogKey { get; }

    public Guid PublicationId { get; }

    public CatalogPublicationActivationBlockReason Reason { get; }

    public string RequiredAction { get; }
}
