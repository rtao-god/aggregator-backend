using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Aggregator.Ingestion.Collector.Contracts;

namespace Ingestion.Collector.Api.Tests;

public sealed class CollectorApiContractTests
{
    [Fact]
    public async Task AnonymousCollectorSubmissionIsRejectedBeforeStoreWrite()
    {
        using var factory = new CollectorApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/collector-candidates",
            CreateRequest(),
            AcceptanceJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(factory.Store.Candidate);
    }

    [Fact]
    public async Task MissingSubmitScopeIsForbiddenBeforeStoreWrite()
    {
        using var factory = new CollectorApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(CollectorApiFactory.AuthenticationHeader, "true");
        client.DefaultRequestHeaders.Add(CollectorApiFactory.ScopesHeader, "ingestion.review");

        using var response = await client.PostAsJsonAsync(
            "/api/collector-candidates",
            CreateRequest(),
            AcceptanceJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(factory.Store.Candidate);
    }

    [Fact]
    public async Task ExactSubmitScopeAllowsBoundedCollectorCommand()
    {
        using var factory = new CollectorApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(CollectorApiFactory.AuthenticationHeader, "true");
        client.DefaultRequestHeaders.Add(CollectorApiFactory.ScopesHeader, "ingestion.submit");

        using var response = await client.PostAsJsonAsync(
            "/api/collector-candidates",
            CreateRequest(),
            AcceptanceJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidate = await response.Content.ReadFromJsonAsync<CollectorCandidateResponse>(
            AcceptanceJson.Options);
        Assert.NotNull(candidate);
        Assert.NotNull(factory.Store.Candidate);
        Assert.Equal(candidate.CandidateId, factory.Store.Candidate.CandidateId);
    }

    [Fact]
    public async Task UnknownJsonPropertyIsRejectedBeforeStoreWrite()
    {
        using var factory = new CollectorApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(CollectorApiFactory.AuthenticationHeader, "true");
        client.DefaultRequestHeaders.Add(CollectorApiFactory.ScopesHeader, "ingestion.submit");
        var request = CreateRequest();
        var json = $$"""
            {
              "commandId":"{{request.CommandId}}",
              "sourceSystem":"acceptance-fixture",
              "sourceReference":"https://collector.example/fixture/studio",
              "observedAtUtc":"{{request.ObservedAtUtc:O}}",
              "kind":"place",
              "externalId":"studio-example",
              "title":"Beispiel Tonstudio",
              "website":"https://example.test/studio",
              "hourlyPrice":80,
              "evidenceDigest":"{{request.EvidenceDigest}}",
              "unexpected":true
            }
            """;
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/collector-candidates", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(factory.Store.Candidate);
    }

    private static SubmitCollectorCandidateRequest CreateRequest() =>
        new(
            Guid.Parse("0198f700-0000-7000-8000-000000000001"),
            "acceptance-fixture",
            "https://collector.example/fixture/studio",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            CollectorCandidateKindContract.Place,
            "studio-example",
            "Beispiel Tonstudio",
            "https://example.test/studio",
            80m,
            Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes("fixture"))));
}

internal static class AcceptanceJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = Create();

    private static System.Text.Json.JsonSerializerOptions Create()
    {
        var options = new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter(
                System.Text.Json.JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}
