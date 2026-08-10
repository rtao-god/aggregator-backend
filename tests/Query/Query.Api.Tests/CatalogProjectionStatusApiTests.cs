using System.Net;
using System.Text.Json;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Api.Tests;

public sealed class CatalogProjectionStatusApiTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 8, 10, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadyStatusReturnsExactPointersWithoutReadingListings()
    {
        using var factory = new QueryApiFactory();
        var snapshot = CreateSnapshot();
        factory.ProjectionStatusStore.Snapshot = snapshot;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/catalog-query/catalogs/berlin-recording-services/projection-status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "max-age=15",
            response.Headers.CacheControl?.ToString() ?? string.Empty,
            StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("berlin-recording-services", root.GetProperty("catalogKey").GetString());
        Assert.Equal("ready", root.GetProperty("state").GetString());
        Assert.Equal("QUERY_PROJECTION_READY", root.GetProperty("code").GetString());
        Assert.Equal(
            snapshot.PublicReadRevision!.Id,
            root.GetProperty("publicRead")
                .GetProperty("metadata")
                .GetProperty("publicReadRevisionId")
                .GetGuid());
        Assert.Equal(
            "ready",
            root.GetProperty("sitemap").GetProperty("state").GetString());
        Assert.Equal(
            snapshot.SitemapPublicReadRevisionId,
            root.GetProperty("sitemap").GetProperty("publicReadRevisionId").GetGuid());
        Assert.Equal(7, root.GetProperty("catalogSourceActivationRevision").GetInt64());
        Assert.Equal(0, root.GetProperty("activeReadBlockCount").GetInt32());
        Assert.Equal(1, factory.ProjectionStatusStore.ReadCount);
        Assert.Equal(
            "berlin-recording-services",
            factory.ProjectionStatusStore.LastCatalogKey);
        Assert.Equal(0, factory.Store.PageReadCount);
        Assert.Equal(0, factory.Store.RouteReadCount);
    }

    [Fact]
    public async Task ActiveReadBlockReturnsExplicitBlockedState()
    {
        using var factory = new QueryApiFactory();
        var blockedAtUtc = CreatedAtUtc.AddMinutes(5);
        factory.ProjectionStatusStore.Snapshot = CreateSnapshot(
            activeReadBlockCount: 1,
            oldestReadBlockAtUtc: blockedAtUtc);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/catalog-query/catalogs/berlin-recording-services/projection-status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("blocked", root.GetProperty("state").GetString());
        Assert.Equal("QUERY_PROJECTION_BLOCKED", root.GetProperty("code").GetString());
        Assert.Equal(
            "blocked",
            root.GetProperty("publicRead").GetProperty("state").GetString());
        Assert.Equal(1, root.GetProperty("activeReadBlockCount").GetInt32());
        Assert.Equal(
            blockedAtUtc,
            root.GetProperty("oldestReadBlockAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task MissingSitemapReturnsDegradedStateWithoutInventedRevision()
    {
        using var factory = new QueryApiFactory();
        factory.ProjectionStatusStore.Snapshot = CreateSnapshot(includeSitemap: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/catalog-query/catalogs/berlin-recording-services/projection-status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("degraded", root.GetProperty("state").GetString());
        Assert.Equal(
            "QUERY_SITEMAP_PROJECTION_MISSING",
            root.GetProperty("code").GetString());
        var sitemap = root.GetProperty("sitemap");
        Assert.Equal("missing", sitemap.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, sitemap.GetProperty("publicReadRevisionId").ValueKind);
    }

    [Fact]
    public async Task UnknownProjectionEvidenceReturnsTypedNotFound()
    {
        using var factory = new QueryApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/catalog-query/catalogs/unknown-catalog/projection-status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "QUERY_PROJECTION_STATUS_NOT_FOUND",
            document.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, factory.ProjectionStatusStore.ReadCount);
        Assert.Equal(0, factory.Store.PageReadCount);
    }

    private static PublicProjectionStatusSnapshot CreateSnapshot(
        bool includeSitemap = true,
        int activeReadBlockCount = 0,
        DateTimeOffset? oldestReadBlockAtUtc = null)
    {
        var baseProjectionId = Guid.Parse("01990410-0000-7000-8000-000000000002");
        var sourcePublicationId = Guid.Parse("01990410-0000-7000-8000-000000000005");
        var currentRevision = PublicReadRevision.Restore(
            Guid.Parse("01990410-0000-7000-8000-000000000001"),
            "berlin-recording-services",
            baseProjectionId,
            Guid.Parse("01990410-0000-7000-8000-000000000003"),
            Guid.Parse("01990410-0000-7000-8000-000000000004"),
            sourcePublicationId,
            CreatedAtUtc,
            new string('a', 64));
        return new PublicProjectionStatusSnapshot(
            "berlin-recording-services",
            currentRevision,
            PublicReadActivationRevision: 12,
            PublicReadActivatedAtUtc: CreatedAtUtc.AddMinutes(2),
            CatalogSourceActivationRevision: 7,
            CatalogCheckpointPublicReadRevisionId:
                Guid.Parse("01990410-0000-7000-8000-000000000006"),
            CatalogCheckpointBaseProjectionId: baseProjectionId,
            CatalogCheckpointSourcePublicationId: sourcePublicationId,
            CatalogCheckpointUpdatedAtUtc: CreatedAtUtc.AddMinutes(1),
            activeReadBlockCount,
            oldestReadBlockAtUtc,
            includeSitemap ? currentRevision.Id : null,
            includeSitemap ? 2 : null,
            includeSitemap ? CreatedAtUtc.AddMinutes(3) : null,
            includeSitemap ? CreatedAtUtc.AddMinutes(4) : null);
    }
}
