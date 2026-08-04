using Aggregator.Catalog.Domain;

namespace Catalog.Domain.Tests;

public sealed class ListingTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SubjectFactoriesKeepPlaceAndProviderBindingsExclusive()
    {
        var placeId = PlaceId.New();
        var providerId = ProviderId.New();

        var place = ListingSubject.ForPlace(placeId);
        var provider = ListingSubject.ForProvider(providerId);

        var placeSubject = Assert.IsType<PlaceListingSubject>(place);
        var providerSubject = Assert.IsType<ProviderListingSubject>(provider);
        Assert.Equal(ListingKind.Place, placeSubject.Kind);
        Assert.Equal(placeId, placeSubject.PlaceId);
        Assert.Equal(ListingKind.Provider, providerSubject.Kind);
        Assert.Equal(providerId, providerSubject.ProviderId);
    }

    [Fact]
    public void AttachingDraftNeverChangesPublishedPointer()
    {
        var actor = ActorId.New();
        var listing = Listing.Create(
            ListingId.New(),
            CatalogId.New(),
            ListingSubject.ForPlace(PlaceId.New()),
            actor,
            Timestamp);

        listing.AttachDraftRevision(ListingRevisionId.New(), listing.AggregateRevision, actor, Timestamp);

        Assert.Equal(ListingLifecycleState.Draft, listing.State);
        Assert.NotNull(listing.CurrentDraftRevisionId);
        Assert.Null(listing.CurrentPublishedRevisionId);
        Assert.Null(listing.CurrentPublicationId);
    }

    [Fact]
    public void StaleAggregateRevisionIsRejected()
    {
        var actor = ActorId.New();
        var listing = Listing.Create(
            ListingId.New(),
            CatalogId.New(),
            ListingSubject.ForProvider(ProviderId.New()),
            actor,
            Timestamp);
        var staleRevision = listing.AggregateRevision;
        listing.AttachDraftRevision(ListingRevisionId.New(), staleRevision, actor, Timestamp);

        var exception = Assert.Throws<CatalogDomainException>(() =>
            listing.SubmitForReview(staleRevision, actor, Timestamp));

        Assert.Equal("LISTING_REVISION_CONFLICT", exception.Code);
    }

    [Fact]
    public void PublicationActivatesExactApprovedDraftOnly()
    {
        var actor = ActorId.New();
        var listing = Listing.Create(
            ListingId.New(),
            CatalogId.New(),
            ListingSubject.ForPlace(PlaceId.New()),
            actor,
            Timestamp);
        var selectedRevision = ListingRevisionId.New();
        listing.AttachDraftRevision(selectedRevision, listing.AggregateRevision, actor, Timestamp);
        listing.SubmitForReview(listing.AggregateRevision, actor, Timestamp);
        listing.Approve(listing.AggregateRevision, actor, Timestamp);
        listing.RequestPublication(listing.AggregateRevision, actor, Timestamp);

        var exception = Assert.Throws<CatalogDomainException>(() =>
            listing.MarkPublished(
                ListingRevisionId.New(),
                PublicationId.New(),
                listing.AggregateRevision,
                actor,
                Timestamp));

        Assert.Equal("LISTING_PUBLICATION_REVISION_MISMATCH", exception.Code);
        Assert.Null(listing.CurrentPublishedRevisionId);
    }
}
