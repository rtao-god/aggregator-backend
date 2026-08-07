using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>Classifies the current Catalog owner state that prevents publication activation.</summary>
public enum CatalogPublicationActivationBlockReason
{
    PointerIdentityMismatch = 1,
    MediaNotPublishable = 2,
    PublicVisibilitySuppression = 3,
}

/// <summary>Reports a fail-closed publication activation rejected by the final Catalog database boundary.</summary>
public sealed class CatalogPublicationActivationBlockedException : InvalidOperationException
{
    public CatalogPublicationActivationBlockedException(
        CatalogKey catalogKey,
        Guid publicationId,
        CatalogPublicationActivationBlockReason reason,
        string message,
        string requiredAction)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(catalogKey);
        if (publicationId == Guid.Empty)
        {
            throw new ArgumentException("Publication ID is required.", nameof(publicationId));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Publication activation block reason is unsupported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredAction);
        CatalogKey = catalogKey.Value;
        PublicationId = publicationId;
        Reason = reason;
        RequiredAction = requiredAction.Trim();
    }

    public string CatalogKey { get; }

    public Guid PublicationId { get; }

    public CatalogPublicationActivationBlockReason Reason { get; }

    public string RequiredAction { get; }
}
