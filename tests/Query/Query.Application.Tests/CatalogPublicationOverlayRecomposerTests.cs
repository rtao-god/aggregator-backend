using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class CatalogPublicationOverlayRecomposerTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecompositionPreservesExactOverlayIdentitiesAndChangesOnlyBase()
    {
        var baseProjection = QueryBaseProjection.Create(
            Guid.Parse("0198ff00-0000-7000-8000-000000000001"),
            "berlin-recording-services",
            QueryLocalePolicy.Create("de-DE", ["de-DE", "en-GB"]),
            Guid.Parse("0198ff00-0000-7000-8000-000000000002"),
            new string('1', 64),
            2,
            "query-publication-builder",
            Timestamp,
            [],
            new string('2', 64));
        var promotion = QueryOverlayRevision.Create(
            Guid.Parse("0198ff00-0000-7000-8000-000000000003"),
            baseProjection.CatalogKey,
            QueryOverlayKind.Promotion,
            7,
            Timestamp.AddMinutes(-2),
            new string('3', 64),
            4);
        var safety = QueryOverlayRevision.Create(
            Guid.Parse("0198ff00-0000-7000-8000-000000000004"),
            baseProjection.CatalogKey,
            QueryOverlayKind.VisibilitySafety,
            5,
            Timestamp.AddMinutes(-1),
            new string('4', 64),
            2);
        var revisionId = Guid.Parse("0198ff00-0000-7000-8000-000000000005");

        var first = CatalogPublicationOverlayRecomposer.Compose(
            baseProjection,
            promotion,
            safety,
            revisionId,
            Timestamp);
        var second = CatalogPublicationOverlayRecomposer.Compose(
            baseProjection,
            promotion,
            safety,
            revisionId,
            Timestamp);

        Assert.Equal(baseProjection.Id, first.BaseProjectionId);
        Assert.Equal(promotion.Id, first.PromotionOverlayId);
        Assert.Equal(safety.Id, first.SafetyOverlayId);
        Assert.Equal(baseProjection.SourcePublicationId, first.SourcePublicationId);
        Assert.Equal(first.ContentDigest, second.ContentDigest);
    }

    [Fact]
    public void RecompositionRejectsForeignOverlayOwner()
    {
        var baseProjection = QueryBaseProjection.Create(
            Guid.Parse("0198ff00-0000-7000-8000-000000000010"),
            "berlin-recording-services",
            QueryLocalePolicy.Create("de-DE", ["de-DE"]),
            Guid.Parse("0198ff00-0000-7000-8000-000000000011"),
            new string('5', 64),
            2,
            "query-publication-builder",
            Timestamp,
            [],
            new string('6', 64));
        var promotion = QueryOverlayRevision.Create(
            Guid.Parse("0198ff00-0000-7000-8000-000000000012"),
            "foreign-catalog",
            QueryOverlayKind.Promotion,
            1,
            Timestamp,
            new string('7', 64),
            0);
        var safety = QueryOverlayRevision.Create(
            Guid.Parse("0198ff00-0000-7000-8000-000000000013"),
            baseProjection.CatalogKey,
            QueryOverlayKind.VisibilitySafety,
            1,
            Timestamp,
            new string('8', 64),
            0);

        var exception = Assert.Throws<QueryProjectionException>(() =>
            CatalogPublicationOverlayRecomposer.Compose(
                baseProjection,
                promotion,
                safety,
                Guid.Parse("0198ff00-0000-7000-8000-000000000014"),
                Timestamp));

        Assert.Equal("QUERY_PUBLICATION_OVERLAY_CATALOG_MISMATCH", exception.Code);
    }
}
