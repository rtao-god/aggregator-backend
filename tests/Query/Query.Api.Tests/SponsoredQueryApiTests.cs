using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aggregator.Promotion.Contracts;

namespace Query.Api.Tests;

public sealed class SponsoredQueryApiTests
{
    [Fact]
    public async Task MatchingRevisionReturnsSponsoredOverlayAndCacheIdentity()
    {
        using var factory = new SponsoredQueryApiFactory();
        var publicReadRevisionId = Guid.Parse("0198fd00-0000-7000-8000-000000000001");
        var overlayId = Guid.Parse("0198fd00-0000-7000-8000-000000000002");
        factory.Store.Response = new SponsoredListingSearchResponse(
            overlayId,
            publicReadRevisionId,
            [
                new SponsoredListingResponse(
                    overlayId,
                    publicReadRevisionId,
                    Guid.Parse("0198fd00-0000-7000-8000-000000000003"),
                    Guid.Parse("0198fd00-0000-7000-8000-000000000004"),
                    1,
                    "de-DE",
                    "Gesponsertes Studio",
                    "/de-DE/listings/sponsored",
                    "Anzeige"),
            ]);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/catalog-query/catalogs/berlin-recording-services/sponsored?publicReadRevisionId={publicReadRevisionId:D}&locale=de-DE");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.True(response.Headers.TryGetValues("X-Promotion-Overlay-Id", out var overlayValues));
        Assert.Contains(overlayId.ToString("D"), overlayValues);
        var body = await response.Content.ReadFromJsonAsync<SponsoredListingSearchResponse>();
        Assert.NotNull(body);
        Assert.Equal(overlayId, body.OverlayId);
        Assert.Single(body.Sponsored);
        Assert.Equal(1, factory.Store.ReadCount);
    }

    [Fact]
    public async Task RevisionWithoutMatchingOverlayReturnsTypedUnavailable()
    {
        using var factory = new SponsoredQueryApiFactory();
        using var client = factory.CreateClient();
        var publicReadRevisionId = Guid.Parse("0198fd00-0000-7000-8000-000000000010");

        using var response = await client.GetAsync(
            $"/api/catalog-query/catalogs/berlin-recording-services/sponsored?publicReadRevisionId={publicReadRevisionId:D}&locale=en-GB");
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "QUERY_SPONSORED_OVERLAY_UNAVAILABLE",
            body.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, factory.Store.ReadCount);
    }

    [Fact]
    public async Task OverlayFromAnotherRevisionReturnsTypedUnavailable()
    {
        using var factory = new SponsoredQueryApiFactory();
        var requestedRevisionId = Guid.Parse("0198fd00-0000-7000-8000-000000000011");
        var actualRevisionId = Guid.Parse("0198fd00-0000-7000-8000-000000000012");
        factory.Store.Response = new SponsoredListingSearchResponse(
            Guid.Parse("0198fd00-0000-7000-8000-000000000013"),
            actualRevisionId,
            Array.Empty<SponsoredListingResponse>());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/catalog-query/catalogs/berlin-recording-services/sponsored?publicReadRevisionId={requestedRevisionId:D}&locale=en-GB");
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "QUERY_SPONSORED_REVISION_MISMATCH",
            body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnsupportedFilterIsRejectedBeforeStoreRead()
    {
        using var factory = new SponsoredQueryApiFactory();
        using var client = factory.CreateClient();
        var publicReadRevisionId = Guid.Parse("0198fd00-0000-7000-8000-000000000020");

        using var response = await client.GetAsync(
            $"/api/catalog-query/catalogs/berlin-recording-services/sponsored?publicReadRevisionId={publicReadRevisionId:D}&locale=de-DE&unknown=true");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Store.ReadCount);
    }

    [Fact]
    public async Task MatchingIfNoneMatchReturnsNotModified()
    {
        using var factory = new SponsoredQueryApiFactory();
        var publicReadRevisionId = Guid.Parse("0198fd00-0000-7000-8000-000000000030");
        factory.Store.Response = new SponsoredListingSearchResponse(
            Guid.Parse("0198fd00-0000-7000-8000-000000000031"),
            publicReadRevisionId,
            Array.Empty<SponsoredListingResponse>());
        using var client = factory.CreateClient();
        using var first = await client.GetAsync(
            $"/api/catalog-query/catalogs/berlin-recording-services/sponsored?publicReadRevisionId={publicReadRevisionId:D}&locale=de-DE");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(first.Headers.ETag);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/catalog-query/catalogs/berlin-recording-services/sponsored?publicReadRevisionId={publicReadRevisionId:D}&locale=de-DE");
        request.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag.ToString());
        using var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
