using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Api;
using Aggregator.Catalog.Contracts;

namespace Catalog.Api.Tests;

public sealed class CatalogApiContractTests(CatalogApiFactory factory) : IClassFixture<CatalogApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly Guid SubjectId = Guid.Parse("0192f5f0-0000-7000-8000-000000000001");
    private static readonly Guid SubjectRevisionId = Guid.Parse("0192f5f0-0000-7000-8000-000000000002");
    private static readonly Guid ActorId = Guid.Parse("0192f5f0-0000-7000-8000-000000000003");

    [Fact]
    public async Task LivenessIsReadOnlyAndAnonymous()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Catalog.Api", document.RootElement.GetProperty("owner").GetString());
        Assert.Equal("live", document.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task CommandWithoutAuthenticationReturnsTypedOwnerFailure()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/catalog-command/catalogs/config-revisions")
        {
            Content = JsonContent.Create(new { }, options: JsonOptions),
        };

        using var response = await client.SendAsync(request);
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Catalog.Access", document.RootElement.GetProperty("owner").GetString());
        Assert.Equal("AUTHENTICATION_REQUIRED", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AuthenticatedCommandWithoutActorMappingFailsClosed()
    {
        using var client = factory.CreateClient();
        using var request = CreateListingRequest("catalog", "catalog", includeActor: false);

        using var response = await client.SendAsync(request);
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("ACTOR_MAPPING_REQUIRED", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RouteAndBodyCatalogMismatchReturnsTypedContractFailure()
    {
        using var client = factory.CreateClient();
        using var request = CreateListingRequest("route-catalog", "body-catalog", includeActor: true);

        using var response = await client.SendAsync(request);
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "catalog.listing_route_catalog_mismatch",
            document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task NumericEnumTokenIsRejectedByWireContract()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/catalog-command/catalogs/catalog/listings")
        {
            Content = JsonContent.Create(new
            {
                catalogKey = "catalog",
                subject = new
                {
                    subjectId = SubjectId,
                    subjectRevisionId = SubjectRevisionId,
                    kind = 2,
                },
            }),
        };
        Authenticate(request, includeActor: true);

        using var response = await client.SendAsync(request);
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("CATALOG_REQUEST_INVALID", document.RootElement.GetProperty("code").GetString());
    }

    private static HttpRequestMessage CreateListingRequest(
        string routeCatalog,
        string bodyCatalog,
        bool includeActor)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/catalog-command/catalogs/{routeCatalog}/listings")
        {
            Content = JsonContent.Create(
                new CreateListingRequest(
                    bodyCatalog,
                    new SubjectReferenceContract(
                        SubjectId,
                        SubjectRevisionId,
                        SubjectKindContract.Place)),
                options: JsonOptions),
        };
        Authenticate(request, includeActor);
        return request;
    }

    private static void Authenticate(HttpRequestMessage request, bool includeActor)
    {
        request.Headers.Add(CatalogApiFactory.AuthenticationHeader, "true");
        request.Headers.Add(
            CatalogApiFactory.ScopesHeader,
            CatalogAuthorizationPolicies.EditListing);
        if (includeActor)
        {
            request.Headers.Add(CatalogApiFactory.ActorHeader, ActorId.ToString("D"));
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}
