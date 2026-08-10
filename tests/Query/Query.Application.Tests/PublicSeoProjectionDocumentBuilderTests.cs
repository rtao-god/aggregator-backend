using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class PublicSeoProjectionDocumentBuilderTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid PublicationId =
        Guid.Parse("01990f40-0000-7000-8000-000000000001");

    [Fact]
    public void ExactCanonicalTargetProducesSeparateSitemapAndRedirectDocuments()
    {
        var documents = PublicSeoProjectionDocumentBuilder.Build(
        [
            Canonical("de-DE", "/de-DE/listings/current"),
            Canonical("en-GB", "/en-GB/listings/current"),
            Redirect(
                "de-DE",
                "/de-DE/listings/legacy",
                "/de-DE/listings/current"),
        ]);

        Assert.Equal(2, documents.SitemapRecords.Count);
        Assert.Single(documents.Redirects);
        var redirect = documents.Redirects[0];
        Assert.Equal("recording-services", redirect.CatalogKey.Value);
        Assert.Equal("de-DE", redirect.Locale.Value);
        Assert.Equal("/de-DE/listings/legacy", redirect.SourcePath.Value);
        Assert.Equal("/de-DE/listings/current", redirect.TargetPath.Value);
        Assert.Equal(PublicationId, redirect.SourcePublicationId);
        Assert.Equal("canonical route changed", redirect.Reason);
        Assert.Equal(Timestamp, redirect.CreatedAtUtc);
    }

    [Fact]
    public void SelfTargetIsRejectedExplicitly()
    {
        var exception = Assert.Throws<QuerySitemapProjectionException>(() =>
            PublicSeoProjectionDocumentBuilder.Build(
            [
                Redirect(
                    "de-DE",
                    "/de-DE/listings/legacy",
                    "/de-DE/listings/legacy"),
            ]));

        Assert.Equal("QUERY_SEO_REDIRECT_SELF_TARGET", exception.Code);
    }

    [Fact]
    public void RedirectChainIsRejectedBeforePersistence()
    {
        var exception = Assert.Throws<QuerySitemapProjectionException>(() =>
            PublicSeoProjectionDocumentBuilder.Build(
            [
                Canonical("de-DE", "/de-DE/listings/current"),
                Redirect(
                    "de-DE",
                    "/de-DE/listings/legacy-a",
                    "/de-DE/listings/legacy-b"),
                Redirect(
                    "de-DE",
                    "/de-DE/listings/legacy-b",
                    "/de-DE/listings/current"),
            ]));

        Assert.Equal("QUERY_SEO_REDIRECT_CHAIN_FORBIDDEN", exception.Code);
    }

    [Fact]
    public void RedirectCycleIsRejectedBeforeTargetResolution()
    {
        var exception = Assert.Throws<QuerySitemapProjectionException>(() =>
            PublicSeoProjectionDocumentBuilder.Build(
            [
                Redirect(
                    "de-DE",
                    "/de-DE/listings/legacy-a",
                    "/de-DE/listings/legacy-b"),
                Redirect(
                    "de-DE",
                    "/de-DE/listings/legacy-b",
                    "/de-DE/listings/legacy-a"),
            ]));

        Assert.Equal("QUERY_SEO_REDIRECT_LOOP", exception.Code);
    }

    [Fact]
    public void MissingExactCanonicalTargetIsRejected()
    {
        var exception = Assert.Throws<QuerySitemapProjectionException>(() =>
            PublicSeoProjectionDocumentBuilder.Build(
            [
                Canonical("de-DE", "/de-DE/listings/current"),
                Redirect(
                    "de-DE",
                    "/de-DE/listings/legacy",
                    "/de-DE/listings/missing"),
            ]));

        Assert.Equal("QUERY_SEO_REDIRECT_TARGET_MISSING", exception.Code);
    }

    [Fact]
    public void RedirectCannotCrossRouteGroupIdentity()
    {
        var exception = Assert.Throws<QuerySitemapProjectionException>(() =>
            PublicSeoProjectionDocumentBuilder.Build(
            [
                Canonical(
                    "de-DE",
                    "/de-DE/listings/current",
                    routeGroupKey: "listing:target"),
                Redirect(
                    "de-DE",
                    "/de-DE/listings/legacy",
                    "/de-DE/listings/current",
                    routeGroupKey: "listing:source"),
            ]));

        Assert.Equal("QUERY_SEO_REDIRECT_TARGET_IDENTITY_MISMATCH", exception.Code);
    }

    private static PublicSeoRouteSource Canonical(
        string locale,
        string path,
        string routeGroupKey = "listing:exact") =>
        new(
            QuerySeoRouteKind.Listing,
            routeGroupKey,
            "recording-services",
            locale,
            path,
            Timestamp,
            IsDraft: false,
            RedirectTargetPath: null,
            IsSuppressed: false);

    private static PublicSeoRouteSource Redirect(
        string locale,
        string sourcePath,
        string targetPath,
        string routeGroupKey = "listing:exact") =>
        new(
            QuerySeoRouteKind.Listing,
            routeGroupKey,
            "recording-services",
            locale,
            sourcePath,
            Timestamp,
            IsDraft: false,
            RedirectTargetPath: targetPath,
            IsSuppressed: false,
            RedirectSourcePublicationId: PublicationId,
            RedirectReason: "canonical route changed",
            RedirectCreatedAtUtc: Timestamp);
}
