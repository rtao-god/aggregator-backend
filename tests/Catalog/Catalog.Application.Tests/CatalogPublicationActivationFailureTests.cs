using Aggregator.Catalog.Application;
using Aggregator.Catalog.Domain;

namespace Catalog.Application.Tests;

public sealed class CatalogPublicationActivationFailureTests
{
    private static readonly CatalogKey CatalogKey =
        Aggregator.Catalog.Domain.CatalogKey.Create("berlin-recording-services");
    private static readonly Guid PublicationId =
        Guid.Parse("0198ff00-0000-7000-8000-000000000001");

    [Theory]
    [InlineData(
        CatalogPublicationActivationBlockReason.PointerIdentityMismatch,
        "CATALOG_PUBLICATION_POINTER_IDENTITY_MISMATCH")]
    [InlineData(
        CatalogPublicationActivationBlockReason.MediaNotPublishable,
        "CATALOG_PUBLICATION_MEDIA_NOT_PUBLISHABLE")]
    [InlineData(
        CatalogPublicationActivationBlockReason.PublicVisibilitySuppression,
        "CATALOG_PUBLICATION_VISIBILITY_SUPPRESSED")]
    public void ActivationBlockMapsToTypedCatalogPublicationFailure(
        CatalogPublicationActivationBlockReason reason,
        string expectedCode)
    {
        var exception = new CatalogPublicationActivationBlockedException(
            CatalogKey,
            PublicationId,
            reason,
            "Publication activation was rejected by the final Catalog owner gate.",
            "Correct the blocking Catalog owner state before retrying.");

        Assert.True(CatalogFailureTranslator.TryTranslate(exception, out var failure));
        Assert.Equal("Catalog.Publications", failure.Owner);
        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(409, failure.StatusCode);
        Assert.Equal(exception.Message, failure.Detail);
        Assert.Equal(exception.RequiredAction, failure.RequiredAction);
        Assert.Equal(CatalogKey.Value, failure.Context["catalogKey"]);
        Assert.Equal(PublicationId, failure.Context["publicationId"]);
        Assert.Equal(reason.ToString(), failure.Context["blockReason"]);
    }

    [Fact]
    public void ActivationBlockRequiresExactOwnerIdentityAndAction()
    {
        Assert.Throws<ArgumentException>(() => new CatalogPublicationActivationBlockedException(
            CatalogKey,
            Guid.Empty,
            CatalogPublicationActivationBlockReason.PointerIdentityMismatch,
            "Blocked.",
            "Reload."));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogPublicationActivationBlockedException(
            CatalogKey,
            PublicationId,
            (CatalogPublicationActivationBlockReason)999,
            "Blocked.",
            "Reload."));
        Assert.Throws<ArgumentException>(() => new CatalogPublicationActivationBlockedException(
            CatalogKey,
            PublicationId,
            CatalogPublicationActivationBlockReason.PointerIdentityMismatch,
            "Blocked.",
            " "));
    }
}
