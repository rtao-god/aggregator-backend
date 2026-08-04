using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Ingestion.Api;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;

namespace Ingestion.Api.Tests;

public sealed class IngestionRegistrationReplayTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task RegistrationReplayReturnsOriginalResultAfterBatchAdvances()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        var registration = CreateRegistration(factory);
        using var firstRequest = CreateRegistrationRequest(registration, "registration-replay-after-advance");
        using var firstResponse = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<IngestionBatchRegistrationResponse>(JsonOptions);
        Assert.NotNull(first);

        using var prepareRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/ingestion/batches/{first.Batch.Id:D}/upload-request")
        {
            Content = JsonContent.Create(
                new PrepareIngestionUploadRequest(first.Batch.AggregateRevision),
                options: JsonOptions),
        };
        AddAuthorization(prepareRequest);
        prepareRequest.Headers.Add("Idempotency-Key", "prepare-before-registration-replay");
        using var prepareResponse = await client.SendAsync(prepareRequest);
        Assert.Equal(HttpStatusCode.OK, prepareResponse.StatusCode);
        var prepared = await prepareResponse.Content.ReadFromJsonAsync<IngestionUploadAuthorizationDto>(JsonOptions);
        Assert.NotNull(prepared);
        Assert.Equal(ImportBatchStateContract.Uploading, prepared.Batch.State);

        using var replayRequest = CreateRegistrationRequest(
            registration,
            "registration-replay-after-advance");
        using var replayResponse = await client.SendAsync(replayRequest);

        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<IngestionBatchRegistrationResponse>(JsonOptions);
        Assert.NotNull(replay);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Batch, replay.Batch);
        Assert.Equal(ImportBatchStateContract.Registered, replay.Batch.State);
        Assert.Equal(1, replay.Batch.AggregateRevision);
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

    private static HttpRequestMessage CreateRegistrationRequest(
        RegisterIngestionBatchRequest body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingestion/batches")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        AddAuthorization(request);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static void AddAuthorization(HttpRequestMessage request)
    {
        request.Headers.Add(IngestionApiFactory.AuthenticationHeader, "authenticated");
        request.Headers.Add(IngestionApiFactory.SubjectHeader, "collector-service");
        request.Headers.Add(
            IngestionApiFactory.ScopesHeader,
            IngestionAuthorizationPolicies.Upload);
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
