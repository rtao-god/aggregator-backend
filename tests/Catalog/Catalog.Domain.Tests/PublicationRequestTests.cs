using Aggregator.Catalog.Domain;

namespace Catalog.Domain.Tests;

public sealed class PublicationRequestTests
{
    [Fact]
    public void DuplicateListingSelectionIsRejected()
    {
        var listingId = ListingId.New();
        var actor = ActorId.New();
        var timestamp = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

        var exception = Assert.Throws<CatalogDomainException>(() =>
            CatalogPublicationRequest.Create(
                PublicationRequestId.New(),
                PublicationId.New(),
                CatalogId.New(),
                null,
                ProductConfigurationRevisionId.New(),
                TaxonomyRevisionId.New(),
                AttributeRevisionId.New(),
                MarketAreaRevisionId.New(),
                [
                    new SelectedListingRevision(listingId, ListingRevisionId.New()),
                    new SelectedListingRevision(listingId, ListingRevisionId.New()),
                ],
                "publish approved listings",
                actor,
                timestamp));

        Assert.Equal("PUBLICATION_LISTING_DUPLICATE", exception.Code);
    }

    [Fact]
    public void OnlyProcessingRequestCanBecomeSealed()
    {
        var request = CatalogPublicationRequest.Create(
            PublicationRequestId.New(),
            PublicationId.New(),
            CatalogId.New(),
            null,
            ProductConfigurationRevisionId.New(),
            TaxonomyRevisionId.New(),
            AttributeRevisionId.New(),
            MarketAreaRevisionId.New(),
            [new SelectedListingRevision(ListingId.New(), ListingRevisionId.New())],
            "publish approved listings",
            ActorId.New(),
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero));

        var exception = Assert.Throws<CatalogDomainException>(() => request.MarkSealed(request.AggregateRevision));

        Assert.Equal("PUBLICATION_REQUEST_STATE_INVALID", exception.Code);
    }
}
