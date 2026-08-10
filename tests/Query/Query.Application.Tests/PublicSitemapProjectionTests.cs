using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class PublicSitemapProjectionTests
{
    private static readonly Guid PublicReadRevisionId =
        Guid.Parse("01990f20-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InputOrderDoesNotChangeCanonicalDigest()
    {
        var de = CreateDocument(
            "de-DE",
            "/de-DE/studios/exact-studio",
            [
                ("de-DE", "/de-DE/studios/exact-studio"),
                ("en-GB", "/en-GB/studios/exact-studio"),
            ]);
        var en = CreateDocument(
            "en-GB",
            "/en-GB/studios/exact-studio",
            [
                ("de-DE", "/de-DE/studios/exact-studio"),
                ("en-GB", "/en-GB/studios/exact-studio"),
            ]);
        var firstStore = new CapturingStore();
        var secondStore = new CapturingStore();

        await new BuildPublicSitemapProjectionService(firstStore).BuildAndActivateAsync(
            PublicReadRevisionId,
            "recording-services",
            [en, de],
            Timestamp,
            CancellationToken.None);
        await new BuildPublicSitemapProjectionService(secondStore).BuildAndActivateAsync(
            PublicReadRevisionId,
            "recording-services",
            [de, en],
            Timestamp,
            CancellationToken.None);

        Assert.NotNull(firstStore.Artifact);
        Assert.NotNull(secondStore.Artifact);
        Assert.Equal(firstStore.Artifact!.ContentDigest, secondStore.Artifact!.ContentDigest);
        Assert.Matches("^[0-9a-f]{64}$", firstStore.Artifact.ContentDigest);
        Assert.Equal(
            new[] { "de-DE", "en-GB" },
            firstStore.Artifact.Records.Select(record => record.Locale.Value));
    }

    [Fact]
    public async Task MissingReverseHreflangEdgeBlocksProjection()
    {
        var de = CreateDocument(
            "de-DE",
            "/de-DE/studios/exact-studio",
            [
                ("de-DE", "/de-DE/studios/exact-studio"),
                ("en-GB", "/en-GB/studios/exact-studio"),
            ]);
        var en = CreateDocument(
            "en-GB",
            "/en-GB/studios/exact-studio",
            [("en-GB", "/en-GB/studios/exact-studio")]);
        var service = new BuildPublicSitemapProjectionService(new CapturingStore());

        var exception = await Assert.ThrowsAsync<QuerySitemapProjectionException>(() =>
            service.BuildAndActivateAsync(
                PublicReadRevisionId,
                "recording-services",
                [de, en],
                Timestamp,
                CancellationToken.None));

        Assert.Equal("QUERY_SITEMAP_HREFLANG_NOT_RECIPROCAL", exception.Code);
    }

    [Fact]
    public async Task MissingHreflangTargetBlocksProjection()
    {
        var service = new BuildPublicSitemapProjectionService(new CapturingStore());
        var de = CreateDocument(
            "de-DE",
            "/de-DE/studios/exact-studio",
            [
                ("de-DE", "/de-DE/studios/exact-studio"),
                ("en-GB", "/en-GB/studios/exact-studio"),
            ]);

        var exception = await Assert.ThrowsAsync<QuerySitemapProjectionException>(() =>
            service.BuildAndActivateAsync(
                PublicReadRevisionId,
                "recording-services",
                [de],
                Timestamp,
                CancellationToken.None));

        Assert.Equal("QUERY_SITEMAP_HREFLANG_TARGET_MISSING", exception.Code);
    }

    [Fact]
    public async Task EmptyCatalogStillCreatesExplicitRevisionArtifact()
    {
        var store = new CapturingStore();
        var service = new BuildPublicSitemapProjectionService(store);

        var result = await service.BuildAndActivateAsync(
            PublicReadRevisionId,
            "recording-services",
            Array.Empty<QuerySitemapDocument>(),
            Timestamp,
            CancellationToken.None);

        Assert.Equal(PublicSitemapProjectionDisposition.Applied, result.Disposition);
        Assert.NotNull(store.Artifact);
        Assert.Empty(store.Artifact!.Records);
        Assert.Matches("^[0-9a-f]{64}$", store.Artifact.ContentDigest);
    }

    private static QuerySitemapDocument CreateDocument(
        string locale,
        string path,
        IReadOnlyCollection<(string Locale, string Path)> alternates) =>
        QuerySitemapDocument.CreateIndexable(
            QuerySeoRouteKind.Listing,
            "recording-services",
            locale,
            path,
            path,
            alternates
                .Select(item => QueryHreflangRoute.Create(item.Locale, item.Path))
                .ToArray(),
            Timestamp,
            isDraft: false,
            redirectsToAnotherRoute: false,
            isSuppressed: false);

    private sealed class CapturingStore : IPublicSitemapProjectionStore
    {
        public PublicSitemapProjectionArtifact? Artifact { get; private set; }

        public Task<PublicSitemapProjectionResult> ActivateAsync(
            PublicSitemapProjectionArtifact artifact,
            CancellationToken cancellationToken)
        {
            Artifact = artifact;
            return Task.FromResult(new PublicSitemapProjectionResult(
                artifact.PublicReadRevisionId,
                PublicSitemapProjectionDisposition.Applied));
        }
    }
}
