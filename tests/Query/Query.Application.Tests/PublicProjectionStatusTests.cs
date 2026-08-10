using Aggregator.Query.Application;
using Aggregator.Query.Contracts;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class PublicProjectionStatusTests
{
    private static readonly Guid CurrentRevisionId =
        Guid.Parse("01990400-0000-7000-8000-000000000001");
    private static readonly Guid CheckpointRevisionId =
        Guid.Parse("01990400-0000-7000-8000-000000000002");
    private static readonly Guid BaseProjectionId =
        Guid.Parse("01990400-0000-7000-8000-000000000003");
    private static readonly Guid SourcePublicationId =
        Guid.Parse("01990400-0000-7000-8000-000000000004");
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 8, 10, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CurrentReadAndMatchingSitemapAreReady()
    {
        var store = new StubStore(CreateSnapshot());
        var service = new ReadPublicProjectionStatusService(store);

        var result = await service.ReadAsync(
            "berlin-recording-services",
            CancellationToken.None);

        Assert.Equal(PublicProjectionStatusStateContract.Ready, result.State);
        Assert.Equal("QUERY_PROJECTION_READY", result.Code);
        Assert.Equal(
            PublicProjectionComponentStateContract.Ready,
            result.PublicRead.State);
        Assert.Equal(CurrentRevisionId, result.PublicRead.Metadata?.PublicReadRevisionId);
        Assert.Equal(12, result.PublicRead.ActivationRevision);
        Assert.Equal(
            PublicProjectionComponentStateContract.Ready,
            result.Sitemap.State);
        Assert.Equal(CurrentRevisionId, result.Sitemap.PublicReadRevisionId);
        Assert.Equal(2, result.Sitemap.RecordCount);
        Assert.Equal(7, result.CatalogSourceActivationRevision);
        Assert.Equal(0, result.ActiveReadBlockCount);
        Assert.Null(result.OldestReadBlockAtUtc);
        Assert.Equal("berlin-recording-services", store.LastCatalogKey);
    }

    [Fact]
    public async Task ActiveReadBlockOverridesOtherwiseReadyComponents()
    {
        var blockedAtUtc = CreatedAtUtc.AddMinutes(5);
        var store = new StubStore(CreateSnapshot(
            activeReadBlockCount: 2,
            oldestReadBlockAtUtc: blockedAtUtc));
        var service = new ReadPublicProjectionStatusService(store);

        var result = await service.ReadAsync(
            "berlin-recording-services",
            CancellationToken.None);

        Assert.Equal(PublicProjectionStatusStateContract.Blocked, result.State);
        Assert.Equal("QUERY_PROJECTION_BLOCKED", result.Code);
        Assert.Equal(
            PublicProjectionComponentStateContract.Blocked,
            result.PublicRead.State);
        Assert.Equal(2, result.ActiveReadBlockCount);
        Assert.Equal(blockedAtUtc, result.OldestReadBlockAtUtc);
    }

    [Fact]
    public async Task MissingSitemapIsExplicitlyDegraded()
    {
        var store = new StubStore(CreateSnapshot(includeSitemap: false));
        var service = new ReadPublicProjectionStatusService(store);

        var result = await service.ReadAsync(
            "berlin-recording-services",
            CancellationToken.None);

        Assert.Equal(PublicProjectionStatusStateContract.Degraded, result.State);
        Assert.Equal("QUERY_SITEMAP_PROJECTION_MISSING", result.Code);
        Assert.Equal(
            PublicProjectionComponentStateContract.Missing,
            result.Sitemap.State);
        Assert.Null(result.Sitemap.PublicReadRevisionId);
    }

    [Fact]
    public async Task SitemapForAnotherPublicReadRevisionIsExplicitlyStale()
    {
        var staleRevisionId = Guid.Parse("01990400-0000-7000-8000-000000000099");
        var store = new StubStore(CreateSnapshot(
            sitemapPublicReadRevisionId: staleRevisionId));
        var service = new ReadPublicProjectionStatusService(store);

        var result = await service.ReadAsync(
            "berlin-recording-services",
            CancellationToken.None);

        Assert.Equal(PublicProjectionStatusStateContract.Degraded, result.State);
        Assert.Equal("QUERY_SITEMAP_PROJECTION_STALE", result.Code);
        Assert.Equal(
            PublicProjectionComponentStateContract.Stale,
            result.Sitemap.State);
        Assert.Equal(staleRevisionId, result.Sitemap.PublicReadRevisionId);
    }

    [Fact]
    public async Task MissingQueryEvidenceIsNotSuccessfulUnavailableData()
    {
        var service = new ReadPublicProjectionStatusService(new StubStore(null));

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => service.ReadAsync(
            "berlin-recording-services",
            CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("QUERY_PROJECTION_STATUS_NOT_FOUND", exception.Code);
    }

    [Fact]
    public async Task SourceCheckpointMayReferenceAnOlderOverlayRevisionOfSameBase()
    {
        var store = new StubStore(CreateSnapshot());
        var service = new ReadPublicProjectionStatusService(store);

        var result = await service.ReadAsync(
            "berlin-recording-services",
            CancellationToken.None);

        Assert.Equal(PublicProjectionStatusStateContract.Ready, result.State);
        Assert.NotEqual(CheckpointRevisionId, result.PublicRead.Metadata?.PublicReadRevisionId);
        Assert.Equal(BaseProjectionId, result.PublicRead.Metadata?.BaseProjectionId);
    }

    [Fact]
    public async Task CheckpointFromAnotherBasePublicationIsCorruption()
    {
        var store = new StubStore(CreateSnapshot(
            checkpointBaseProjectionId:
                Guid.Parse("01990400-0000-7000-8000-000000000098")));
        var service = new ReadPublicProjectionStatusService(store);

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => service.ReadAsync(
            "berlin-recording-services",
            CancellationToken.None));

        Assert.Equal(500, exception.StatusCode);
        Assert.Equal("QUERY_PROJECTION_STATUS_CHECKPOINT_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task BlockCountWithoutOldestBlockTimestampIsCorruption()
    {
        var store = new StubStore(CreateSnapshot(
            activeReadBlockCount: 1,
            oldestReadBlockAtUtc: null));
        var service = new ReadPublicProjectionStatusService(store);

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => service.ReadAsync(
            "berlin-recording-services",
            CancellationToken.None));

        Assert.Equal("QUERY_PROJECTION_STATUS_BLOCK_SHAPE_INVALID", exception.Code);
    }

    private static PublicProjectionStatusSnapshot CreateSnapshot(
        bool includeSitemap = true,
        Guid? sitemapPublicReadRevisionId = null,
        int activeReadBlockCount = 0,
        DateTimeOffset? oldestReadBlockAtUtc = null,
        Guid? checkpointBaseProjectionId = null)
    {
        var revision = PublicReadRevision.Restore(
            CurrentRevisionId,
            "berlin-recording-services",
            BaseProjectionId,
            Guid.Parse("01990400-0000-7000-8000-000000000005"),
            Guid.Parse("01990400-0000-7000-8000-000000000006"),
            SourcePublicationId,
            CreatedAtUtc,
            new string('a', 64));
        return new PublicProjectionStatusSnapshot(
            "berlin-recording-services",
            revision,
            PublicReadActivationRevision: 12,
            PublicReadActivatedAtUtc: CreatedAtUtc.AddMinutes(2),
            CatalogSourceActivationRevision: 7,
            CatalogCheckpointPublicReadRevisionId: CheckpointRevisionId,
            CatalogCheckpointBaseProjectionId:
                checkpointBaseProjectionId ?? BaseProjectionId,
            CatalogCheckpointSourcePublicationId: SourcePublicationId,
            CatalogCheckpointUpdatedAtUtc: CreatedAtUtc.AddMinutes(1),
            activeReadBlockCount,
            oldestReadBlockAtUtc,
            includeSitemap
                ? sitemapPublicReadRevisionId ?? CurrentRevisionId
                : null,
            includeSitemap ? 2 : null,
            includeSitemap ? CreatedAtUtc.AddMinutes(3) : null,
            includeSitemap ? CreatedAtUtc.AddMinutes(4) : null);
    }

    private sealed class StubStore(
        PublicProjectionStatusSnapshot? snapshot) : IPublicProjectionStatusStore
    {
        public string? LastCatalogKey { get; private set; }

        public Task<PublicProjectionStatusSnapshot?> ReadAsync(
            string catalogKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCatalogKey = catalogKey;
            return Task.FromResult(snapshot);
        }
    }
}
