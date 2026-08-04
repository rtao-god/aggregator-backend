using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Promotion.Api;
using Aggregator.Promotion.Contracts;

namespace Promotion.Api.Tests;

public sealed class PromotionApiContractTests(PromotionApiFactory factory) : IClassFixture<PromotionApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly int[] NumericPresentationFeatures = [1];
    private static readonly string[] StringPresentationFeatures = ["featuredListing"];
    private static readonly PromotionPresentationFeatureContract[] ProductPresentationFeatures =
        [PromotionPresentationFeatureContract.FeaturedListing];
    private static readonly Guid ActorId =
        Guid.Parse("0198b300-0000-7000-8000-000000000001");

    [Fact]
    public async Task LivenessIsReadOnlyAndAnonymous()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Promotion.Runtime", document.RootElement.GetProperty("owner").GetString());
        Assert.Equal("live", document.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task AnonymousCommandReturnsTypedAuthenticationFailure()
    {
        using var client = factory.CreateClient();
        using var request = CreateProductCommand(
            "promotion-anonymous-product",
            "promotion-anonymous-key",
            authenticate: false,
            includeActor: false,
            includeIdempotency: true);

        using var response = await client.SendAsync(request);
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Promotion.Access", document.RootElement.GetProperty("owner").GetString());
        Assert.Equal("AUTHENTICATION_REQUIRED", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AuthenticatedCommandWithoutActorMappingFailsClosed()
    {
        using var client = factory.CreateClient();
        using var request = CreateProductCommand(
            "promotion-no-actor-product",
            "promotion-no-actor-key",
            authenticate: true,
            includeActor: false,
            includeIdempotency: true);

        using var response = await client.SendAsync(request);
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "PROMOTION_ACTOR_MAPPING_REQUIRED",
            document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CommandWithoutIdempotencyKeyReturnsTypedContractFailure()
    {
        using var client = factory.CreateClient();
        using var request = CreateProductCommand(
            "promotion-no-idempotency-product",
            idempotencyKey: null,
            authenticate: true,
            includeActor: true,
            includeIdempotency: false);

        using var response = await client.SendAsync(request);
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "PROMOTION_IDEMPOTENCY_KEY_REQUIRED",
            document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task NumericEnumTokenIsRejectedByWireContract()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/promotion/products")
        {
            Content = JsonContent.Create(new
            {
                contractIdentity = PromotionContractIdentity.AdminApi,
                contractRevision = PromotionContractIdentity.AdminApiRevision,
                key = "promotion-numeric-enum-product",
                displayNames = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["en-GB"] = "Numeric enum product",
                },
                presentationFeatures = NumericPresentationFeatures,
                requiresVerifiedContact = false,
                requiredContactCapability = (string?)null,
            }),
        };
        Authenticate(request, PromotionAuthorizationPolicies.ManageCatalog, includeActor: true);
        request.Headers.Add("Idempotency-Key", "promotion-numeric-enum-key");

        using var response = await client.SendAsync(request);
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "PROMOTION_REQUEST_CONTRACT_INVALID",
            document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownRequestMemberIsRejectedInsteadOfIgnored()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/promotion/products")
        {
            Content = JsonContent.Create(new
            {
                contractIdentity = PromotionContractIdentity.AdminApi,
                contractRevision = PromotionContractIdentity.AdminApiRevision,
                key = "promotion-extra-member-product",
                displayNames = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["en-GB"] = "Extra member product",
                },
                presentationFeatures = StringPresentationFeatures,
                requiresVerifiedContact = false,
                requiredContactCapability = (string?)null,
                unsupportedProductionField = true,
            }),
        };
        Authenticate(request, PromotionAuthorizationPolicies.ManageCatalog, includeActor: true);
        request.Headers.Add("Idempotency-Key", "promotion-extra-member-key");

        using var response = await client.SendAsync(request);
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "PROMOTION_REQUEST_CONTRACT_INVALID",
            document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ProductCreateAndReplayReturnExactFirstIdentity()
    {
        using var client = factory.CreateClient();
        var before = factory.Backend.ProductCount;
        using var firstRequest = CreateProductCommand(
            "promotion-api-replay-product",
            "promotion-api-replay-key",
            authenticate: true,
            includeActor: true,
            includeIdempotency: true);
        using var replayRequest = CreateProductCommand(
            "promotion-api-replay-product",
            "promotion-api-replay-key",
            authenticate: true,
            includeActor: true,
            includeIdempotency: true);

        using var firstResponse = await client.SendAsync(firstRequest);
        using var replayResponse = await client.SendAsync(replayRequest);
        var first = await firstResponse.Content.ReadFromJsonAsync<PromotionProductResponse>(JsonOptions);
        var replay = await replayResponse.Content.ReadFromJsonAsync<PromotionProductResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.NotNull(first);
        Assert.NotNull(replay);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.CurrentRevision.Id, replay.CurrentRevision.Id);
        Assert.Equal(before + 1, factory.Backend.ProductCount);

        using var readRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/promotion/products/{first.Id:D}");
        Authenticate(readRequest, PromotionAuthorizationPolicies.Read, includeActor: false);
        using var readResponse = await client.SendAsync(readRequest);
        var read = await readResponse.Content.ReadFromJsonAsync<PromotionProductResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.NotNull(read);
        Assert.Equal(first.Id, read.Id);
    }

    private static HttpRequestMessage CreateProductCommand(
        string productKey,
        string? idempotencyKey,
        bool authenticate,
        bool includeActor,
        bool includeIdempotency)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/promotion/products")
        {
            Content = JsonContent.Create(
                new CreatePromotionProductRequest(
                    PromotionContractIdentity.AdminApi,
                    PromotionContractIdentity.AdminApiRevision,
                    productKey,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["de-DE"] = "Hervorgehobener Eintrag",
                        ["en-GB"] = "Featured listing",
                    },
                    ProductPresentationFeatures,
                    RequiresVerifiedContact: true,
                    RequiredContactCapability: "website"),
                options: JsonOptions),
        };
        if (authenticate)
        {
            Authenticate(request, PromotionAuthorizationPolicies.ManageCatalog, includeActor);
        }

        if (includeIdempotency)
        {
            request.Headers.Add(
                "Idempotency-Key",
                idempotencyKey ?? throw new InvalidOperationException("Test idempotency key is required."));
        }

        return request;
    }

    private static void Authenticate(
        HttpRequestMessage request,
        string scope,
        bool includeActor)
    {
        request.Headers.Add(PromotionApiFactory.AuthenticationHeader, "true");
        request.Headers.Add(PromotionApiFactory.ScopesHeader, scope);
        if (includeActor)
        {
            request.Headers.Add(PromotionApiFactory.ActorHeader, ActorId.ToString("D"));
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
