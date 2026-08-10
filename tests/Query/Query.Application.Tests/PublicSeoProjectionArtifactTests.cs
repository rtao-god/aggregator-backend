using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class PublicSeoProjectionArtifactTests
{
    private static readonly Guid RevisionId =
        Guid.Parse("01990f60-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PermanentRedirectParticipatesInCanonicalArtifactDigest()
    {
        var record = CreateRecord();
        var withoutRedirect = PublicSitemapProjectionArtifactBuilder.Build(
            RevisionId,
            expectedCurrentPublicReadRevisionId: null,
            "recording-services",
            [record],
            Array.Empty<QueryRouteRedirectDocument>(),
            Timestamp);
        var redirect = QueryRouteRedirectDocument.CreatePermanent(
            "recording-services",
            "de-DE",
            "/de-DE/listings/legacy",
            record.Path.Value,
            Guid.Parse("01990f60-0000-7000-8000-000000000002"),
            "canonical route changed",
            Timestamp);
        var withRedirect = PublicSitemapProjectionArtifactBuilder.Build(
            RevisionId,
            expectedCurrentPublicReadRevisionId: null,
            "recording-services",
            [record],
            [redirect],
            Timestamp);

        Assert.NotEqual(withoutRedirect.ContentDigest, withRedirect.ContentDigest);
        Assert.Single(withRedirect.Redirects);
        Assert.Matches("^[0-9a-f]{64}$", withRedirect.ContentDigest);
    }

    [Fact]
    public void RedirectCreationAfterBuildTimeIsRejected()
    {
        var record = CreateRecord();
        var redirect = QueryRouteRedirectDocument.CreatePermanent(
            "recording-services",
            "de-DE",
            "/de-DE/listings/legacy",
            record.Path.Value,
            Guid.Parse("01990f60-0000-7000-8000-000000000003"),
            "canonical route changed",
            Timestamp.AddMinutes(1));

        var exception = Assert.Throws<QuerySitemapProjectionException>(() =>
            PublicSitemapProjectionArtifactBuilder.Build(
                RevisionId,
                expectedCurrentPublicReadRevisionId: null,
                "recording-services",
                [record],
                [redirect],
                Timestamp));

        Assert.Equal("QUERY_SEO_REDIRECT_CREATED_IN_FUTURE", exception.Code);
    }

    private static QuerySitemapDocument CreateRecord() =>
        QuerySitemapDocument.CreateIndexable(
            QuerySeoRouteKind.Listing,
            "recording-services",
            "de-DE",
            "/de-DE/listings/current",
            "/de-DE/listings/current",
            [QueryHreflangRoute.Create("de-DE", "/de-DE/listings/current")],
            Timestamp,
            isDraft: false,
            redirectsToAnotherRoute: false,
            isSuppressed: false);
}
