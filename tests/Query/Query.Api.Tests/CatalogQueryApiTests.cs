using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Api.Tests;

public sealed class CatalogQueryApiTests
{
    [Fact]
    public async Task SearchReturnsRevisionMetadataTypedFiltersFacetsAndCacheIdentity()
    {
        using var factory = new QueryApiFactory();
        factory.Store.Page = CreatePageSnapshot();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/catalog-query/catalogs/berlin-recording-services/listings" +
            "?locale=en-GB&category=recording-studio&district=mitte" +
            "&marketZone=primary_market&listingKind=place&contactKind=website&pageSize=20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.True(response.Headers.TryGetValues("X-Public-Read-Revision-Id", out var revisionValues));
        Assert.Single(revisionValues);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(
            CreateRevision().Id,
            root.GetProperty("metadata").GetProperty("publicReadRevisionId").GetGuid());
        var query = root.GetProperty("query");
        Assert.Equal("recording-studio", query.GetProperty("categoryKey").GetString());
        Assert.Equal("mitte", query.GetProperty("districtKey").GetString());
        Assert.Equal("place", query.GetProperty("listingKind").GetString());
        Assert.Equal("website", query.GetProperty("contactKind").GetString());
        Assert.Equal("primaryMarket", query.GetProperty("marketZone").GetString());
        var listing = root.GetProperty("organic")[0];
        Assert.Equal("fallback", listing.GetProperty("translationState").GetString());
        Assert.Equal("de-DE", listing.GetProperty("resolvedLocale").GetString());
        var sponsored = root.GetProperty("sponsored")[0];
        Assert.Equal(
            CreateListing().ListingId,
            sponsored.GetProperty("listing").GetProperty("listingId").GetGuid());
        Assert.Equal("sponsored", sponsored.GetProperty("disclosureLabelKey").GetString());
        Assert.Equal(1, root.GetProperty("categoryFacets")[0].GetProperty("count").GetInt32());
        Assert.Equal("mitte", root.GetProperty("districtFacets")[0].GetProperty("key").GetString());
        Assert.Equal("place", root.GetProperty("listingKindFacets")[0].GetProperty("value").GetString());
        Assert.Equal("website", root.GetProperty("contactKindFacets")[0].GetProperty("value").GetString());
        Assert.Contains(
            "stale-while-revalidate",
            response.Headers.CacheControl?.ToString() ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal("en-GB", factory.Store.LastCriteria?.RequestedLocale);
        Assert.Equal("recording-studio", factory.Store.LastCriteria?.CategoryKey);
        Assert.Equal("mitte", factory.Store.LastCriteria?.DistrictKey);
        Assert.Equal(QueryListingKind.Place, factory.Store.LastCriteria?.ListingKind);
        Assert.Equal(QueryContactKind.Website, factory.Store.LastCriteria?.ContactKind);
        Assert.Equal(QueryGeographyState.PrimaryMarket, factory.Store.LastCriteria?.MarketZone);
        Assert.Equal(factory.Clock.UtcNow, factory.Store.LastReadAtUtc);
        Assert.Equal(1, factory.Store.PageReadCount);
    }

    [Fact]
    public async Task UnknownQueryParameterIsRejectedBeforeStoreRead()
    {
        using var factory = new QueryApiFactory();
        factory.Store.Page = CreatePageSnapshot();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/catalog-query/catalogs/berlin-recording-services/listings?locale=de-DE&unknown=true");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("QUERY_FILTER_UNKNOWN", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, factory.Store.PageReadCount);
    }

    [Theory]
    [InlineData("listingKind", "studio")]
    [InlineData("contactKind", "telegram")]
    [InlineData("marketZone", "urban")]
    public async Task UnsupportedTypedFilterIsRejectedBeforeStoreRead(
        string parameterName,
        string value)
    {
        using var factory = new QueryApiFactory();
        factory.Store.Page = CreatePageSnapshot();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/catalog-query/catalogs/berlin-recording-services/listings?{parameterName}={value}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("QUERY_FILTER_INVALID", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, factory.Store.PageReadCount);
    }

    [Fact]
    public async Task MissingProjectionIsUnavailableNotEmptySuccess()
    {
        using var factory = new QueryApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/catalog-query/catalogs/berlin-recording-services/listings?locale=de-DE");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("QUERY_PROJECTION_UNAVAILABLE", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MatchingIfNoneMatchReturnsNotModifiedForExactRequestRevision()
    {
        using var factory = new QueryApiFactory();
        factory.Store.Page = CreatePageSnapshot();
        using var client = factory.CreateClient();
        using var first = await client.GetAsync(
            "/api/catalog-query/catalogs/berlin-recording-services/listings?locale=de-DE");
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/catalog-query/catalogs/berlin-recording-services/listings?locale=de-DE");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag.Tag));
        using var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task RouteReturnsExactCardFromSamePublicReadRevision()
    {
        using var factory = new QueryApiFactory();
        var revision = CreateRevision();
        var listing = CreateListing();
        factory.Store.Document = new PublicReadDocumentSnapshot(
            revision,
            LocalePolicy(),
            listing);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/catalog-query/catalogs/berlin-recording-services/routes/de-DE/listings/{listing.ListingId:N}?locale=de-DE");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            revision.Id,
            document.RootElement.GetProperty("metadata").GetProperty("publicReadRevisionId").GetGuid());
        Assert.Equal("exact", document.RootElement.GetProperty("listing").GetProperty("translationState").GetString());
        Assert.Equal(1, factory.Store.RouteReadCount);
    }

    private static PublicReadPageSnapshot CreatePageSnapshot()
    {
        var listing = CreateListing();
        return new PublicReadPageSnapshot(
            CreateRevision(),
            LocalePolicy(),
            [listing],
            [new PublicSponsoredListingSnapshot(CreatePlacement(listing.ListingId), listing)],
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["recording-studio"] = 1,
            },
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["mitte"] = 1,
            },
            new Dictionary<QueryListingKind, int>
            {
                [QueryListingKind.Place] = 1,
            },
            new Dictionary<QueryContactKind, int>
            {
                [QueryContactKind.Website] = 1,
            });
    }

    private static PublicReadRevision CreateRevision() =>
        PublicReadRevision.Restore(
            Guid.Parse("0198a500-0000-7000-8000-000000000001"),
            "berlin-recording-services",
            Guid.Parse("0198a500-0000-7000-8000-000000000002"),
            Guid.Parse("0198a500-0000-7000-8000-000000000003"),
            Guid.Parse("0198a500-0000-7000-8000-000000000004"),
            Guid.Parse("0198a500-0000-7000-8000-000000000005"),
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero),
            new string('a', 64));

    private static QueryLocalePolicy LocalePolicy() =>
        QueryLocalePolicy.Create("de-DE", ["de-DE", "en-GB"]);

    private static QueryPromotionPlacement CreatePlacement(Guid listingId) =>
        QueryPromotionPlacement.Create(
            Guid.Parse("0198a500-0000-7000-8000-000000000020"),
            Guid.Parse("0198a500-0000-7000-8000-000000000021"),
            listingId,
            "berlin-recording-services",
            "featured-listing",
            QueryPromotionPlacementScope.Catalog,
            "berlin-recording-services",
            ["de-DE", "en-GB"],
            new DateTimeOffset(2026, 8, 4, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 4, 13, 0, 0, TimeSpan.Zero),
            10,
            1,
            "sponsored",
            QueryPromotionPlacementState.Active,
            2,
            new DateTimeOffset(2026, 8, 4, 11, 30, 0, TimeSpan.Zero));

    private static QueryListingDocument CreateListing()
    {
        var listingId = Guid.Parse("0198a500-0000-7000-8000-000000000010");
        return QueryListingDocument.Create(
            listingId,
            Guid.Parse("0198a500-0000-7000-8000-000000000011"),
            Guid.Parse("0198a500-0000-7000-8000-000000000012"),
            Guid.Parse("0198a500-0000-7000-8000-000000000013"),
            QueryListingKind.Place,
            [
                new QueryLocalizedDocument(
                    "de-DE",
                    $"/de-DE/listings/{listingId:N}",
                    "Studio Beispiel",
                    QueryFieldState.Missing,
                    null),
            ],
            ["recording-studio"],
            [],
            new QueryGeographyDocument(QueryGeographyState.PrimaryMarket, 52.52m, 13.405m, "mitte"),
            [
                new QueryContactDocument(
                    Guid.Parse("0198a500-0000-7000-8000-000000000014"),
                    QueryContactKind.Website,
                    "https://example.test",
                    null),
            ],
            [],
            new string('b', 64),
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero));
    }
}
