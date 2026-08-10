using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class PublicSitemapDocumentBuilderTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LocalizedRouteGroupBuildsReciprocalDocuments()
    {
        var documents = PublicSitemapDocumentBuilder.Build(
        [
            Source("de-DE", "/de-DE/studios/exact-studio"),
            Source("en-GB", "/en-GB/studios/exact-studio"),
        ]);

        Assert.Equal(2, documents.Count);
        Assert.All(documents, document => Assert.Equal(2, document.Hreflang.Count));
        Assert.Equal(
            new[] { "de-DE", "en-GB" },
            documents.Select(document => document.Locale.Value));
        var german = documents[0];
        var english = documents[1];
        Assert.Contains(
            german.Hreflang,
            item => item.Locale.Value == english.Locale.Value &&
                    item.Path.Value == english.Path.Value);
        Assert.Contains(
            english.Hreflang,
            item => item.Locale.Value == german.Locale.Value &&
                    item.Path.Value == german.Path.Value);
    }

    [Fact]
    public void DuplicateLocaleWithinGroupIsRejected()
    {
        var exception = Assert.Throws<QuerySitemapProjectionException>(() =>
            PublicSitemapDocumentBuilder.Build(
            [
                Source("de-DE", "/de-DE/studios/exact-studio"),
                Source("de-DE", "/de-DE/studios/another-studio"),
            ]));

        Assert.Equal("QUERY_SITEMAP_SOURCE_LOCALE_DUPLICATE", exception.Code);
    }

    [Theory]
    [InlineData(true, null, false, "QUERY_SEO_DRAFT_NOT_INDEXABLE")]
    [InlineData(false, "/de-DE/studios/replacement", false, "QUERY_SEO_REDIRECT_NOT_INDEXABLE")]
    [InlineData(false, null, true, "QUERY_SEO_SUPPRESSED_NOT_INDEXABLE")]
    public void NonIndexableSourceBlocksBuild(
        bool isDraft,
        string? redirectTarget,
        bool isSuppressed,
        string expectedCode)
    {
        var source = Source("de-DE", "/de-DE/studios/exact-studio") with
        {
            IsDraft = isDraft,
            RedirectTargetPath = redirectTarget,
            IsSuppressed = isSuppressed,
        };

        var exception = Assert.Throws<QueryDomainException>(() =>
            PublicSitemapDocumentBuilder.Build([source]));

        Assert.Equal(expectedCode, exception.Code);
    }

    private static PublicSeoRouteSource Source(string locale, string path) =>
        new(
            QuerySeoRouteKind.Listing,
            RouteGroupKey: "listing:01990f40-0000-7000-8000-000000000001",
            CatalogKey: "recording-services",
            locale,
            path,
            Timestamp,
            IsDraft: false,
            RedirectTargetPath: null,
            IsSuppressed: false);
}
