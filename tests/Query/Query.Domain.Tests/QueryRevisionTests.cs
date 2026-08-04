using Aggregator.Query.Domain;

namespace Query.Domain.Tests;

public sealed class QueryRevisionTests
{
    [Fact]
    public void BaseProjectionRejectsDuplicatePublicRoute()
    {
        var publishedAt = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var first = CreateDocument(Guid.Parse("0198a100-0000-7000-8000-000000000001"), "/de-DE/listings/shared", publishedAt);
        var second = CreateDocument(Guid.Parse("0198a100-0000-7000-8000-000000000002"), "/de-DE/listings/shared", publishedAt);

        var exception = Assert.Throws<QueryDomainException>(() => QueryBaseProjection.Create(
            Guid.Parse("0198a100-0000-7000-8000-000000000010"),
            "berlin-recording-services",
            LocalePolicy(),
            Guid.Parse("0198a100-0000-7000-8000-000000000011"),
            new string('a', 64),
            1,
            "builder",
            publishedAt,
            [first, second],
            new string('b', 64)));

        Assert.Equal("QUERY_ROUTE_DUPLICATE", exception.Code);
    }

    [Fact]
    public void BaseProjectionRejectsDocumentWithoutDefaultLocale()
    {
        var publishedAt = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var document = QueryListingDocument.Create(
            Guid.Parse("0198a100-0000-7000-8000-000000000012"),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            QueryListingKind.Place,
            [new QueryLocalizedDocument("en-GB", "/en-GB/listings/studio", "Studio", QueryFieldState.Missing, null)],
            ["recording-studio"],
            [],
            new QueryGeographyDocument(QueryGeographyState.PrimaryMarket, 52.5m, 13.4m, "mitte"),
            [],
            [],
            new string('f', 64),
            publishedAt);

        var exception = Assert.Throws<QueryDomainException>(() => QueryBaseProjection.Create(
            Guid.Parse("0198a100-0000-7000-8000-000000000013"),
            "berlin-recording-services",
            LocalePolicy(),
            Guid.Parse("0198a100-0000-7000-8000-000000000014"),
            new string('a', 64),
            1,
            "builder",
            publishedAt,
            [document],
            new string('b', 64)));

        Assert.Equal("QUERY_DEFAULT_LOCALE_DOCUMENT_MISSING", exception.Code);
    }

    [Fact]
    public void PublicReadRevisionRejectsComponentsFromDifferentCatalogs()
    {
        var timestamp = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var document = CreateDocument(Guid.Parse("0198a100-0000-7000-8000-000000000020"), "/de-DE/listings/one", timestamp);
        var projection = QueryBaseProjection.Create(
            Guid.Parse("0198a100-0000-7000-8000-000000000021"),
            "catalog-one",
            LocalePolicy(),
            Guid.Parse("0198a100-0000-7000-8000-000000000022"),
            new string('a', 64),
            1,
            "builder",
            timestamp,
            [document],
            new string('b', 64));
        var promotion = QueryOverlayRevision.CreateEmpty(
            Guid.Parse("0198a100-0000-7000-8000-000000000023"),
            "catalog-two",
            QueryOverlayKind.Promotion,
            0,
            timestamp,
            new string('c', 64));
        var safety = QueryOverlayRevision.CreateEmpty(
            Guid.Parse("0198a100-0000-7000-8000-000000000024"),
            "catalog-one",
            QueryOverlayKind.VisibilitySafety,
            1,
            timestamp,
            new string('d', 64));

        var exception = Assert.Throws<QueryDomainException>(() => PublicReadRevision.Create(
            Guid.Parse("0198a100-0000-7000-8000-000000000025"),
            projection,
            promotion,
            safety,
            timestamp,
            new string('e', 64)));

        Assert.Equal("QUERY_COMPONENT_CATALOG_MISMATCH", exception.Code);
    }

    private static QueryLocalePolicy LocalePolicy() =>
        QueryLocalePolicy.Create("de-DE", ["de-DE", "en-GB"]);

    private static QueryListingDocument CreateDocument(Guid listingId, string routePath, DateTimeOffset publishedAt) =>
        QueryListingDocument.Create(
            listingId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            QueryListingKind.Place,
            [new QueryLocalizedDocument("de-DE", routePath, "Studio", QueryFieldState.Missing, null)],
            ["recording-studio"],
            [],
            new QueryGeographyDocument(QueryGeographyState.PrimaryMarket, 52.5m, 13.4m, "mitte"),
            [],
            [],
            new string('f', 64),
            publishedAt);
}
