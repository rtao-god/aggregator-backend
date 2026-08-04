using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Ingestion.Api;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;

namespace Ingestion.Api.Tests;

public sealed class IngestionUploadApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task RegisteredBatchAuthorizesAndCompletesExactUpload()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        var registered = await RegisterAsync(client, factory);

        using var prepareRequest = CreateCommandRequest(
            HttpMethod.Post,
            $"/api/ingestion/batches/{registered.Batch.Id:D}/upload-request",
            new PrepareIngestionUploadRequest(registered.Batch.AggregateRevision),
            "prepare-upload");
        using var prepareResponse = await client.SendAsync(prepareRequest);

        Assert.Equal(HttpStatusCode.OK, prepareResponse.StatusCode);
        var prepared = await prepareResponse.Content.ReadFromJsonAsync<IngestionUploadAuthorizationDto>(
            JsonOptions);
        Assert.NotNull(prepared);
        Assert.False(prepared.Replayed);
        Assert.Equal(ImportBatchStateContract.Uploading, prepared.Batch.State);
        Assert.Equal(2, prepared.Batch.AggregateRevision);
        Assert.Equal(prepared.Batch.PayloadObjectKey, prepared.ObjectKey);
        Assert.Equal(1, factory.Backend.UploadAuthorizationCount);

        using var completeRequest = CreateCommandRequest(
            HttpMethod.Post,
            $"/api/ingestion/batches/{registered.Batch.Id:D}/upload-complete",
            new CompleteIngestionUploadRequest(prepared.Batch.AggregateRevision),
            "complete-upload");
        using var completeResponse = await client.SendAsync(completeRequest);

        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var completed = await completeResponse.Content.ReadFromJsonAsync<IngestionBatchCommandResponse>(
            JsonOptions);
        Assert.NotNull(completed);
        Assert.False(completed.Replayed);
        Assert.Equal(ImportBatchStateContract.Uploaded, completed.Batch.State);
        Assert.Equal(3, completed.Batch.AggregateRevision);
        Assert.Equal(1, factory.Backend.UploadVerificationCount);
    }

    [Fact]
    public async Task ExactPrepareReplayReturnsStoredTransitionAndNewShortLivedAuthorization()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        var registered = await RegisterAsync(client, factory);
        var path = $"/api/ingestion/batches/{registered.Batch.Id:D}/upload-request";
        var body = new PrepareIngestionUploadRequest(registered.Batch.AggregateRevision);
        using var firstRequest = CreateCommandRequest(HttpMethod.Post, path, body, "prepare-replay");
        using var firstResponse = await client.SendAsync(firstRequest);
        var first = await firstResponse.Content.ReadFromJsonAsync<IngestionUploadAuthorizationDto>(JsonOptions);
        Assert.NotNull(first);

        using var replayRequest = CreateCommandRequest(HttpMethod.Post, path, body, "prepare-replay");
        using var replayResponse = await client.SendAsync(replayRequest);

        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<IngestionUploadAuthorizationDto>(JsonOptions);
        Assert.NotNull(replay);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Batch, replay.Batch);
        Assert.Equal(2, factory.Backend.UploadAuthorizationCount);
    }

    [Fact]
    public async Task ExactCompletionReplayDoesNotReverifyObjectStorage()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        var registered = await RegisterAsync(client, factory);
        var prepared = await PrepareAsync(client, registered);
        var path = $"/api/ingestion/batches/{registered.Batch.Id:D}/upload-complete";
        var body = new CompleteIngestionUploadRequest(prepared.Batch.AggregateRevision);
        using var firstRequest = CreateCommandRequest(HttpMethod.Post, path, body, "complete-replay");
        using var firstResponse = await client.SendAsync(firstRequest);
        var first = await firstResponse.Content.ReadFromJsonAsync<IngestionBatchCommandResponse>(JsonOptions);
        Assert.NotNull(first);

        using var replayRequest = CreateCommandRequest(HttpMethod.Post, path, body, "complete-replay");
        using var replayResponse = await client.SendAsync(replayRequest);

        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<IngestionBatchCommandResponse>(JsonOptions);
        Assert.NotNull(replay);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Batch, replay.Batch);
        Assert.Equal(1, factory.Backend.UploadVerificationCount);
    }

    [Fact]
    public async Task MissingPayloadDoesNotAdvanceUploadingBatch()
    {
        using var factory = new IngestionApiFactory();
        factory.Backend.PayloadExists = false;
        using var client = factory.CreateClient();
        var registered = await RegisterAsync(client, factory);
        var prepared = await PrepareAsync(client, registered);
        using var completeRequest = CreateCommandRequest(
            HttpMethod.Post,
            $"/api/ingestion/batches/{registered.Batch.Id:D}/upload-complete",
            new CompleteIngestionUploadRequest(prepared.Batch.AggregateRevision),
            "complete-missing");

        using var response = await client.SendAsync(completeRequest);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemCodeAsync(response, "INGESTION_PAYLOAD_OBJECT_MISSING");
        using var readRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/ingestion/batches/{registered.Batch.Id:D}");
        AddAuthorization(readRequest, IngestionAuthorizationPolicies.Read);
        using var readResponse = await client.SendAsync(readRequest);
        var batch = await readResponse.Content.ReadFromJsonAsync<IngestionBatchDto>(JsonOptions);
        Assert.NotNull(batch);
        Assert.Equal(ImportBatchStateContract.Uploading, batch.State);
        Assert.Equal(2, batch.AggregateRevision);
    }

    [Fact]
    public async Task StaleCompletionRevisionFailsBeforeObjectVerification()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        var registered = await RegisterAsync(client, factory);
        _ = await PrepareAsync(client, registered);
        using var completeRequest = CreateCommandRequest(
            HttpMethod.Post,
            $"/api/ingestion/batches/{registered.Batch.Id:D}/upload-complete",
            new CompleteIngestionUploadRequest(registered.Batch.AggregateRevision),
            "complete-stale");

        using var response = await client.SendAsync(completeRequest);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemCodeAsync(response, "INGESTION_BATCH_REVISION_CONFLICT");
        Assert.Equal(0, factory.Backend.UploadVerificationCount);
    }

    private static async Task<IngestionBatchRegistrationResponse> RegisterAsync(
        HttpClient client,
        IngestionApiFactory factory)
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
        var body = new RegisterIngestionBatchRequest(
            manifest,
            IngestionPackageValidator.ComputeManifestDigest(manifest));
        using var request = CreateCommandRequest(
            HttpMethod.Post,
            "/api/ingestion/batches",
            body,
            $"register-{manifest.CollectorExportId:D}");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IngestionBatchRegistrationResponse>(JsonOptions)
            ?? throw new InvalidOperationException("Registration response was empty.");
    }

    private static async Task<IngestionUploadAuthorizationDto> PrepareAsync(
        HttpClient client,
        IngestionBatchRegistrationResponse registered)
    {
        using var request = CreateCommandRequest(
            HttpMethod.Post,
            $"/api/ingestion/batches/{registered.Batch.Id:D}/upload-request",
            new PrepareIngestionUploadRequest(registered.Batch.AggregateRevision),
            $"prepare-{registered.Batch.Id:D}");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IngestionUploadAuthorizationDto>(JsonOptions)
            ?? throw new InvalidOperationException("Upload authorization response was empty.");
    }

    private static HttpRequestMessage CreateCommandRequest<T>(
        HttpMethod method,
        string path,
        T body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        AddAuthorization(request, IngestionAuthorizationPolicies.Upload);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static void AddAuthorization(HttpRequestMessage request, string scopes)
    {
        request.Headers.Add(IngestionApiFactory.AuthenticationHeader, "authenticated");
        request.Headers.Add(IngestionApiFactory.SubjectHeader, "collector-service");
        request.Headers.Add(IngestionApiFactory.ScopesHeader, scopes);
    }

    private static async Task AssertProblemCodeAsync(
        HttpResponseMessage response,
        string expectedCode)
    {
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(expectedCode, payload.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            payload.RootElement.GetProperty("correlationId").GetString()));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}
