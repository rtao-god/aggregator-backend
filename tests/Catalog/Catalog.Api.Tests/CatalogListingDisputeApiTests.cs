using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Api;
using Aggregator.Catalog.Contracts;

namespace Catalog.Api.Tests;

public sealed class CatalogListingDisputeApiTests(CatalogApiFactory factory)
    : IClassFixture<CatalogApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly Guid ActorId =
        Guid.Parse("0198ff22-0000-7000-8000-000000000001");

    [Fact]
    public async Task ReviewScopeOpensAndResolvesExactListingDispute()
    {
        using var client = factory.CreateClient();
        var listingId = Guid.CreateVersion7();
        using var openRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/catalog-command/listings/{listingId:D}/disputes");
        openRequest.Content = JsonContent.Create(
            new OpenCatalogListingDisputeRequest(
                ExpectedListingVersion: 1,
                Reason: "Provider contests the published contact facts."),
            options: JsonOptions);

        using var openResponse = await client.SendAsync(openRequest);
        var opened = await openResponse.Content.ReadFromJsonAsync<
            CatalogListingDisputeResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.Created, openResponse.StatusCode);
        Assert.NotNull(opened);
        Assert.Equal(listingId, opened.ListingId);
        Assert.Equal(ListingDisputeStateContract.Open, opened.State);
        Assert.True(opened.BlocksPromotion);
        Assert.Equal(1, opened.AggregateRevision);
        Assert.Equal(ActorId, opened.OpenedByActorId);

        using var resolveRequest = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/catalog-command/listings/{listingId:D}/disputes/" +
            $"{opened.DisputeId:D}/resolution");
        resolveRequest.Content = JsonContent.Create(
            new ResolveCatalogListingDisputeRequest(
                ExpectedDisputeRevision: opened.AggregateRevision,
                ResolutionReason: "Catalog evidence was reviewed and corrected."),
            options: JsonOptions);

        using var resolveResponse = await client.SendAsync(resolveRequest);
        var resolved = await resolveResponse.Content.ReadFromJsonAsync<
            CatalogListingDisputeResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        Assert.NotNull(resolved);
        Assert.Equal(opened.DisputeId, resolved.DisputeId);
        Assert.Equal(ListingDisputeStateContract.Resolved, resolved.State);
        Assert.False(resolved.BlocksPromotion);
        Assert.Equal(2, resolved.AggregateRevision);
        Assert.Equal(ActorId, resolved.ResolvedByActorId);
        Assert.Equal(opened.OpenReason, resolved.OpenReason);
    }

    [Fact]
    public async Task EditListingScopeCannotManageDisputes()
    {
        using var client = factory.CreateClient();
        var listingId = Guid.CreateVersion7();
        using var request = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/catalog-command/listings/{listingId:D}/disputes",
            CatalogAuthorizationPolicies.EditListing);
        request.Content = JsonContent.Create(
            new OpenCatalogListingDisputeRequest(
                ExpectedListingVersion: 1,
                Reason: "Unauthorized dispute command."),
            options: JsonOptions);

        using var response = await client.SendAsync(request);
        var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "AUTHORIZATION_DENIED",
            document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingDisputeReturnsTypedNotFound()
    {
        using var client = factory.CreateClient();
        var listingId = Guid.CreateVersion7();
        var disputeId = Guid.CreateVersion7();
        using var request = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/api/catalog-command/listings/{listingId:D}/disputes/" +
            $"{disputeId:D}/resolution");
        request.Content = JsonContent.Create(
            new ResolveCatalogListingDisputeRequest(
                ExpectedDisputeRevision: 1,
                ResolutionReason: "Missing dispute."),
            options: JsonOptions);

        using var response = await client.SendAsync(request);
        var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "CATALOG_RESOURCE_NOT_FOUND",
            document.RootElement.GetProperty("code").GetString());
    }

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string requestUri,
        string scope = CatalogAuthorizationPolicies.Review)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add(CatalogApiFactory.AuthenticationHeader, "true");
        request.Headers.Add(CatalogApiFactory.ActorHeader, ActorId.ToString("D"));
        request.Headers.Add(CatalogApiFactory.ScopesHeader, scope);
        return request;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}
