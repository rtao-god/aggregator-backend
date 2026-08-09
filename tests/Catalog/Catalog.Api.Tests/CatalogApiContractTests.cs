using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Api;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Media.Contracts;

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
    public async Task CatalogOwnedMediaCommandWithoutActorMappingFailsAtMediaBoundary()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/catalog-command/media/assets")
        {
            Content = JsonContent.Create(
                new RegisterCatalogMediaAssetRequest(
                    "listing_image",
                    "asset.jpg",
                    "image/jpeg",
                    42),
                options: JsonOptions),
        };
        request.Headers.Add(CatalogApiFactory.AuthenticationHeader, "true");
        request.Headers.Add(
            CatalogApiFactory.ScopesHeader,
            CatalogMediaAuthorizationPolicies.ManageMedia);

        using var response = await client.SendAsync(request);
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Catalog.Media.Access", document.RootElement.GetProperty("owner").GetString());
        Assert.Equal("ACTOR_MAPPING_REQUIRED", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CatalogOwnedMediaCommandMapsDomainFailureWithoutLegacyOwnerName()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/catalog-command/media/assets")
        {
            Content = JsonContent.Create(
                new RegisterCatalogMediaAssetRequest(
                    "listing_image",
                    "asset.jpg",
                    "application/pdf",
                    42),
                options: JsonOptions),
        };
        request.Headers.Add(CatalogApiFactory.AuthenticationHeader, "true");
        request.Headers.Add(CatalogApiFactory.ActorHeader, ActorId.ToString("D"));
        request.Headers.Add(
            CatalogApiFactory.ScopesHeader,
            CatalogMediaAuthorizationPolicies.ManageMedia);

        using var response = await client.SendAsync(request);
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Catalog.Media.Domain", document.RootElement.GetProperty("owner").GetString());
        Assert.DoesNotContain(
            "Catalog" + "Media.",
            document.RootElement.GetProperty("owner").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogOwnedMediaRouteRequiresManageMediaScope()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/catalog-command/media/assets")
        {
            Content = JsonContent.Create(
                new RegisterCatalogMediaAssetRequest(
                    "listing_image",
                    "asset.jpg",
                    "image/jpeg",
                    42),
                options: JsonOptions),
        };
        request.Headers.Add(CatalogApiFactory.AuthenticationHeader, "true");
        request.Headers.Add(CatalogApiFactory.ActorHeader, ActorId.ToString("D"));
        request.Headers.Add(
            CatalogApiFactory.ScopesHeader,
            CatalogAuthorizationPolicies.EditListing);

        using var response = await client.SendAsync(request);
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("AUTHORIZATION_DENIED", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ObsoleteMediaApiRouteIsNotReachable()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/catalog" + "-media/assets/0192f5f0-0000-7000-8000-000000000001");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
    public async Task PublicationRequestReturnsDurableOperationResource()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/catalog-command/catalogs/catalog/publication-requests")
        {
            Content = JsonContent.Create(
                new CreateCatalogPublicationRequest(
                    "catalog",
                    Guid.Parse("0192f5f0-0000-7000-8000-000000000010"),
                    new PublicationPointerExpectationContract(
                        PointerExpectationKindContract.Absent,
                        null),
                    [new PublicationSelectionContract(
                        Guid.Parse("0192f5f0-0000-7000-8000-000000000011"),
                        Guid.Parse("0192f5f0-0000-7000-8000-000000000012"),
                        0)]),
                options: JsonOptions),
        };
        request.Headers.Add(CatalogApiFactory.AuthenticationHeader, "true");
        request.Headers.Add(CatalogApiFactory.ActorHeader, ActorId.ToString("D"));
        request.Headers.Add(
            CatalogApiFactory.ScopesHeader,
            CatalogAuthorizationPolicies.Publish);
        request.Headers.Add("Idempotency-Key", "catalog-publication-api-0001");

        using var response = await client.SendAsync(request);
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("pending", document.RootElement.GetProperty("state").GetString());
        var operationId = document.RootElement.GetProperty("operationId").GetGuid();
        Assert.EndsWith(
            $"/api/catalog-command/operations/{operationId:D}",
            response.Headers.Location!.ToString(),
            StringComparison.Ordinal);

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, response.Headers.Location);
        statusRequest.Headers.Add(CatalogApiFactory.AuthenticationHeader, "true");
        statusRequest.Headers.Add(CatalogApiFactory.ActorHeader, ActorId.ToString("D"));
        statusRequest.Headers.Add(
            CatalogApiFactory.ScopesHeader,
            CatalogAuthorizationPolicies.Publish);
        using var statusResponse = await client.SendAsync(statusRequest);
        var statusDocument = await ReadJsonAsync(statusResponse);

        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.Equal(operationId, statusDocument.RootElement.GetProperty("operationId").GetGuid());
        Assert.Equal("pending", statusDocument.RootElement.GetProperty("state").GetString());
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
