using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Ingestion.Api;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;

namespace Ingestion.Api.Tests;

public sealed class IngestionApiContractTests
{
    private static readonly JsonSerializerOptions ClientJsonOptions = CreateClientJsonOptions();

    [Fact]
    public async Task LivenessIsReadOnlyAndAnonymous()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("Ingestion.Runtime", payload.RootElement.GetProperty("owner").GetString());
        Assert.Equal("live", payload.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnonymousRegistrationReturnsTypedAuthenticationFailure()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        using var request = CreateRegistrationRequest(factory, includeAuthentication: false);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertProblemCodeAsync(response, "AUTHENTICATION_REQUIRED", "Ingestion.Access");
    }

    [Fact]
    public async Task RegistrationWithoutUploadScopeReturnsTypedAuthorizationFailure()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        using var request = CreateRegistrationRequest(factory, scopes: IngestionAuthorizationPolicies.Read);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertProblemCodeAsync(response, "AUTHORIZATION_DENIED", "Ingestion.Access");
    }

    [Fact]
    public async Task RegistrationWithoutServiceSubjectFailsClosed()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        using var request = CreateRegistrationRequest(factory, subject: null);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertProblemCodeAsync(
            response,
            "INGESTION_SERVICE_IDENTITY_REQUIRED",
            "Ingestion.Access");
    }

    [Fact]
    public async Task RegistrationWithoutIdempotencyKeyReturnsTypedContractFailure()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        using var request = CreateRegistrationRequest(factory, includeIdempotencyKey: false);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemCodeAsync(
            response,
            "INGESTION_IDEMPOTENCY_KEY_REQUIRED",
            "Ingestion.Commands");
    }

    [Fact]
    public async Task NumericEnumsAreRejectedByWireContract()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        var registration = CreateRegistration(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingestion/batches")
        {
            Content = JsonContent.Create(registration),
        };
        AddAuthorization(request, "collector-service", IngestionAuthorizationPolicies.Upload);
        request.Headers.Add("Idempotency-Key", "numeric-enum-contract");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemCodeAsync(
            response,
            "INGESTION_REQUEST_INVALID",
            "Ingestion.Contracts");
    }

    [Fact]
    public async Task ExactRegistrationIsCreatedAndExactReplayReturnsPriorBatch()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        var registration = CreateRegistration(factory);
        using var firstRequest = CreateRegistrationRequest(
            factory,
            registration: registration,
            idempotencyKey: "register-exact-export");

        using var firstResponse = await client.SendAsync(firstRequest);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<IngestionBatchRegistrationResponse>(
            ClientJsonOptions);
        Assert.NotNull(first);
        Assert.False(first.Replayed);
        Assert.Equal(registration.Manifest.TargetCatalogKey, first.Batch.TargetCatalogKey);
        Assert.Equal("collector-service", factory.Backend.LastCallerServiceIdentity);
        Assert.NotNull(firstResponse.Headers.Location);

        using var replayRequest = CreateRegistrationRequest(
            factory,
            registration: registration,
            idempotencyKey: "register-exact-export");
        using var replayResponse = await client.SendAsync(replayRequest);

        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<IngestionBatchRegistrationResponse>(
            ClientJsonOptions);
        Assert.NotNull(replay);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Batch.Id, replay.Batch.Id);
    }

    [Fact]
    public async Task RegisteredBatchCanBeReadOnlyByExactIdentity()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        using var registerRequest = CreateRegistrationRequest(factory);
        using var registerResponse = await client.SendAsync(registerRequest);
        var registered = await registerResponse.Content.ReadFromJsonAsync<IngestionBatchRegistrationResponse>(
            ClientJsonOptions);
        Assert.NotNull(registered);
        using var readRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/ingestion/batches/{registered.Batch.Id:D}");
        AddAuthorization(readRequest, "collector-service", IngestionAuthorizationPolicies.Read);

        using var response = await client.SendAsync(readRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var batch = await response.Content.ReadFromJsonAsync<IngestionBatchDto>(ClientJsonOptions);
        Assert.NotNull(batch);
        Assert.Equal(registered.Batch.Id, batch.Id);
        Assert.Equal(ImportBatchStateContract.Registered, batch.State);
    }

    [Fact]
    public async Task MissingBatchIsNotSuccessfulEmptyResult()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/ingestion/batches/{Guid.CreateVersion7():D}");
        AddAuthorization(request, "collector-service", IngestionAuthorizationPolicies.Read);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertProblemCodeAsync(response, "INGESTION_BATCH_NOT_FOUND", "Ingestion.Batches");
    }

    private static HttpRequestMessage CreateRegistrationRequest(
        IngestionApiFactory factory,
        RegisterIngestionBatchRequest? registration = null,
        bool includeAuthentication = true,
        string? subject = "collector-service",
        string scopes = IngestionAuthorizationPolicies.Upload,
        bool includeIdempotencyKey = true,
        string idempotencyKey = "register-export")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingestion/batches")
        {
            Content = JsonContent.Create(
                registration ?? CreateRegistration(factory),
                options: ClientJsonOptions),
        };
        if (includeAuthentication)
        {
            AddAuthorization(request, subject, scopes);
        }

        if (includeIdempotencyKey)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private static void AddAuthorization(
        HttpRequestMessage request,
        string? subject,
        string scopes)
    {
        request.Headers.Add(IngestionApiFactory.AuthenticationHeader, "authenticated");
        if (subject is not null)
        {
            request.Headers.Add(IngestionApiFactory.SubjectHeader, subject);
        }

        request.Headers.Add(IngestionApiFactory.ScopesHeader, scopes);
    }

    private static RegisterIngestionBatchRequest CreateRegistration(IngestionApiFactory factory)
    {
        var manifest = new AggregatorCandidateIngestionManifest(
            AggregatorCandidateIngestionContract.Identity,
            AggregatorCandidateIngestionContract.Revision,
            "collector-berlin",
            "build-2026-08-04",
            Guid.CreateVersion7(),
            new string('a', 64),
            "berlin-recording",
            "berlin-recording-services",
            factory.Backend.ActiveConfigurationRevisionId,
            factory.Backend.UtcNow.AddMinutes(-5),
            1,
            new string('b', 64),
            new string('c', 64),
            [
                new IngestionSourcePolicyReferenceContract(
                    "official-website",
                    new string('d', 64),
                    CandidateUsagePolicyContract.Publishable),
            ],
            [
                new IngestionPackageArtifactContract(
                    IngestionArtifactRoleContract.CandidatePayload,
                    "ingestion/quarantine/package.json",
                    new string('e', 64),
                    4_096,
                    "application/json"),
            ]);
        return new RegisterIngestionBatchRequest(
            manifest,
            IngestionPackageValidator.ComputeManifestDigest(manifest));
    }

    private static async Task AssertProblemCodeAsync(
        HttpResponseMessage response,
        string expectedCode,
        string expectedOwner)
    {
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(expectedCode, payload.RootElement.GetProperty("code").GetString());
        Assert.Equal(expectedOwner, payload.RootElement.GetProperty("owner").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            payload.RootElement.GetProperty("correlationId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            payload.RootElement.GetProperty("requiredAction").GetString()));
    }

    private static JsonSerializerOptions CreateClientJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}
