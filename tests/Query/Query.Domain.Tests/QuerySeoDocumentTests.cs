using Aggregator.Query.Domain;

namespace Query.Domain.Tests;

public sealed class QuerySeoDocumentTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExactSelfCanonicalRouteCreatesIndexableDocument()
    {
        var document = QuerySitemapDocument.CreateIndexable(
            QuerySeoRouteKind.Listing,
            "recording-services",
            "de-DE",
            "/de-DE/studios/exact-studio",
            "/de-DE/studios/exact-studio",
            [
                QueryHreflangRoute.Create("de-DE", "/de-DE/studios/exact-studio"),
                QueryHreflangRoute.Create("en-GB", "/en-GB/studios/exact-studio"),
            ],
            Timestamp,
            isDraft: false,
            redirectsToAnotherRoute: false,
            isSuppressed: false);

        Assert.Equal(QuerySeoRouteKind.Listing, document.RouteKind);
        Assert.Equal("recording-services", document.CatalogKey);
        Assert.Equal("de-DE", document.Locale);
        Assert.Equal(document.Path, document.CanonicalPath);
        Assert.Equal(
            new[] { "de-DE", "en-GB" },
            document.Hreflang.Select(item => item.Locale));
        Assert.Equal(Timestamp, document.LastModifiedAtUtc);
    }

    [Theory]
    [InlineData(true, false, false, "QUERY_SEO_DRAFT_NOT_INDEXABLE")]
    [InlineData(false, true, false, "QUERY_SEO_REDIRECT_NOT_INDEXABLE")]
    [InlineData(false, false, true, "QUERY_SEO_SUPPRESSED_NOT_INDEXABLE")]
    public void NonPublicRouteStateCannotEnterSitemap(
        bool isDraft,
        bool redirects,
        bool isSuppressed,
        string expectedCode)
    {
        var exception = Assert.Throws<QueryDomainException>(() =>
            QuerySitemapDocument.CreateIndexable(
                QuerySeoRouteKind.Listing,
                "recording-services",
                "de-DE",
                "/de-DE/studios/exact-studio",
                "/de-DE/studios/exact-studio",
                [QueryHreflangRoute.Create("de-DE", "/de-DE/studios/exact-studio")],
                Timestamp,
                isDraft,
                redirects,
                isSuppressed));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void ArbitraryFilterUrlCannotBecomeIndexable()
    {
        var exception = Assert.Throws<QueryDomainException>(() =>
            QueryHreflangRoute.Create(
                "de-DE",
                "/de-DE/studios?district=mitte"));

        Assert.Equal("QUERY_SEO_PATH_INVALID", exception.Code);
    }

    [Fact]
    public void IndexableRouteMustUseSelfCanonical()
    {
        var exception = Assert.Throws<QueryDomainException>(() =>
            QuerySitemapDocument.CreateIndexable(
                QuerySeoRouteKind.Listing,
                "recording-services",
                "de-DE",
                "/de-DE/studios/exact-studio",
                "/de-DE/studios/another-studio",
                [QueryHreflangRoute.Create("de-DE", "/de-DE/studios/exact-studio")],
                Timestamp,
                isDraft: false,
                redirectsToAnotherRoute: false,
                isSuppressed: false));

        Assert.Equal("QUERY_SEO_CANONICAL_NOT_SELF", exception.Code);
    }

    [Fact]
    public void HreflangMustContainExactSelfRoute()
    {
        var exception = Assert.Throws<QueryDomainException>(() =>
            QuerySitemapDocument.CreateIndexable(
                QuerySeoRouteKind.Listing,
                "recording-services",
                "de-DE",
                "/de-DE/studios/exact-studio",
                "/de-DE/studios/exact-studio",
                [QueryHreflangRoute.Create("en-GB", "/en-GB/studios/exact-studio")],
                Timestamp,
                isDraft: false,
                redirectsToAnotherRoute: false,
                isSuppressed: false));

        Assert.Equal("QUERY_SEO_HREFLANG_SELF_MISSING", exception.Code);
    }

    [Fact]
    public void HreflangCannotContainDuplicateLocale()
    {
        var exception = Assert.Throws<QueryDomainException>(() =>
            QuerySitemapDocument.CreateIndexable(
                QuerySeoRouteKind.Listing,
                "recording-services",
                "de-DE",
                "/de-DE/studios/exact-studio",
                "/de-DE/studios/exact-studio",
                [
                    QueryHreflangRoute.Create("de-DE", "/de-DE/studios/exact-studio"),
                    QueryHreflangRoute.Create("de-DE", "/de-DE/studios/alternate-studio"),
                ],
                Timestamp,
                isDraft: false,
                redirectsToAnotherRoute: false,
                isSuppressed: false));

        Assert.Equal("QUERY_SEO_HREFLANG_LOCALE_DUPLICATE", exception.Code);
    }

    [Fact]
    public void LocaleMustUseExactLanguageRegionShape()
    {
        var exception = Assert.Throws<QueryDomainException>(() =>
            QueryHreflangRoute.Create("de-de", "/de-DE/studios/exact-studio"));

        Assert.Equal("QUERY_SEO_LOCALE_INVALID", exception.Code);
    }
}
