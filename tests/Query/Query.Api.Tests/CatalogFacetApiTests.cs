using System.Net;
using System.Text.Json;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Api.Tests;

public sealed class CatalogFacetApiTests
{
    private const string CatalogKey = "berlin-recording-services";

    [Fact]
    public async Task FacetCatalogReturnsExactRevisionAndTypedCounts()
    {
        using var factory = new QueryApiFactory();
        factory.FacetStore.Snapshot = CreateSnapshot();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/catalog-query/catalogs/{CatalogKey}/facets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.Contains(
            "stale-while-revalidate",
            response.Headers.CacheControl?.ToString() ?? string.Empty,
            StringComparison.Ordinal);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(
            CreateRevision().Id,
            root.GetProperty("metadata").GetProperty("publicReadRevisionId").GetGuid());
        Assert.Equal(
            "recording-studio",
            root.GetProperty("categoryFacets")[0].GetProperty("key").GetString());
        Assert.Equal(
            "place",
            root.GetProperty("listingKindFacets")[0].GetProperty("value").GetString());
        Assert.Equal(
            "website",
            root.GetProperty("contactKindFacets")[0].GetProperty("value").GetString());
        Assert.Equal(
            "primaryMarket",
            root.GetProperty("marketZoneFacets")[0].GetProperty("value").GetString());
        Assert.Equal(CatalogKey, factory.FacetStore.LastCatalogKey);
        Assert.Equal(factory.Clock.UtcNow, factory.FacetStore.LastReadAtUtc);
        Assert.Equal(1, factory.FacetStore.ReadCount);
    }

    [Fact]
    public async Task FacetCatalogRejectsArbitraryQueryParametersBeforeStoreRead()
    {
        using var factory = new QueryApiFactory();
        factory.FacetStore.Snapshot = CreateSnapshot();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/catalog-query/catalogs/{CatalogKey}/facets?category=recording-studio");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "QUERY_FACET_PARAMETER_UNKNOWN",
            document.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, factory.FacetStore.ReadCount);
    }

    [Fact]
    public async Task MissingFacetProjectionIsUnavailableNotEmptySuccess()
    {
        using var factory = new QueryApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/catalog-query/catalogs/{CatalogKey}/facets");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "QUERY_PROJECTION_UNAVAILABLE",
            document.RootElement.GetProperty("code").GetString());
    }

    private static PublicFacetCatalogSnapshot CreateSnapshot() =>
        new(
            CreateRevision(),
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["recording-studio"] = 7,
            },
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["mitte"] = 5,
            },
            new Dictionary<QueryListingKind, int>
            {
                [QueryListingKind.Place] = 7,
            },
            new Dictionary<QueryContactKind, int>
            {
                [QueryContactKind.Website] = 6,
            },
            new Dictionary<QueryGeographyState, int>
            {
                [QueryGeographyState.PrimaryMarket] = 7,
            });

    private static PublicReadRevision CreateRevision() =>
        PublicReadRevision.Restore(
            Guid.Parse("0198ff50-0000-7000-8000-000000000001"),
            CatalogKey,
            Guid.Parse("0198ff50-0000-7000-8000-000000000002"),
            Guid.Parse("0198ff50-0000-7000-8000-000000000003"),
            Guid.Parse("0198ff50-0000-7000-8000-000000000004"),
            Guid.Parse("0198ff50-0000-7000-8000-000000000005"),
            new DateTimeOffset(2026, 8, 10, 17, 55, 0, TimeSpan.Zero),
            new string('b', 64));
}
