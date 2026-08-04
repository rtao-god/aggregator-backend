using Aggregator.Catalog.Application;
using Aggregator.Catalog.Domain;

namespace Catalog.Application.Tests;

public sealed class CatalogPublicationComposerTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SourceOrderDoesNotChangePublicationArtifact()
    {
        var actor = ActorId.New();
        var catalogId = CatalogId.New();
        var first = CreateSource(catalogId, actor, "first-listing", "First listing");
        var second = CreateSource(catalogId, actor, "second-listing", "Second listing");
        var request = CatalogPublicationRequest.Create(
            PublicationRequestId.New(),
            PublicationId.New(),
            catalogId,
            null,
            first.Revision.ProductConfigurationRevisionId,
            first.Revision.TaxonomyRevisionId,
            first.Revision.AttributeRevisionId,
            first.Revision.MarketAreaRevisionId,
            [
                new SelectedListingRevision(first.Listing.Id, first.Revision.Id),
                new SelectedListingRevision(second.Listing.Id, second.Revision.Id),
            ],
            "publish exact approved revisions",
            actor,
            Timestamp);

        var ordered = CatalogPublicationComposer.Compose(request, [first, second], "test-build");
        var reversed = CatalogPublicationComposer.Compose(request, [second, first], "test-build");

        Assert.Equal(
            CatalogCanonicalJson.SerializeToString(ordered),
            CatalogCanonicalJson.SerializeToString(reversed));
        Assert.Equal(2, ordered.ListingCount);
    }

    private static PublicationListingSource CreateSource(
        CatalogId catalogId,
        ActorId actor,
        string categoryKey,
        string title)
    {
        var listing = Listing.Create(
            ListingId.New(),
            catalogId,
            ListingSubject.ForPlace(PlaceId.New()),
            actor,
            Timestamp);
        var revision = ListingRevision.Create(
            ListingRevisionId.New(),
            listing.Id,
            SubjectRevisionId.New(),
            new ProductConfigurationRevisionId(new Guid("0198a6d0-0000-7000-8000-000000000001")),
            new TaxonomyRevisionId(new Guid("0198a6d0-0000-7000-8000-000000000002")),
            new AttributeRevisionId(new Guid("0198a6d0-0000-7000-8000-000000000003")),
            new MarketAreaRevisionId(new Guid("0198a6d0-0000-7000-8000-000000000004")),
            [new LocalizedListingContent("en-GB", title, $"Summary for {title}")],
            [categoryKey],
            Array.Empty<ListingAttributeValue>(),
            [
                new ProvenanceReference(
                    "translations/en-GB/title",
                    "editorial",
                    $"evidence/{listing.Id}/title",
                    ProvenanceUsagePolicy.CommercialAllowed,
                    Timestamp,
                    null),
                new ProvenanceReference(
                    "translations/en-GB/summary",
                    "editorial",
                    $"evidence/{listing.Id}/summary",
                    ProvenanceUsagePolicy.CommercialAllowed,
                    Timestamp,
                    null),
                new ProvenanceReference(
                    $"categories/{categoryKey}",
                    "editorial",
                    $"evidence/{listing.Id}/category",
                    ProvenanceUsagePolicy.CommercialAllowed,
                    Timestamp,
                    null),
            ],
            new string('a', 64),
            actor,
            Timestamp);
        listing.AttachDraftRevision(revision.Id, listing.AggregateRevision, actor, Timestamp);
        return new PublicationListingSource(listing, revision);
    }
}
