using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Api;
using Aggregator.Catalog.Contracts;

namespace Catalog.Ingestion.Api.Tests;

public sealed class CatalogIngestionApiContractTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 13, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task AnonymousDraftCommandReturnsTypedAuthenticationFailure()
    {
        using var factory = new CatalogIngestionApiFactory();
        using var client = factory.CreateClient();
        var command = CreateCommand();
        client.DefaultRequestHeaders.Add("Idempotency-Key", command.CommandId.ToString("D"));

        var response = await client.PostAsJsonAsync(
            "/api/catalog-command/ingestion/drafts",
            command,
            JsonOptions);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Catalog.Access", problem.RootElement.GetProperty("owner").GetString());
        Assert.Equal("AUTHENTICATION_REQUIRED", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WrongScopeCannotCreateCatalogDraft()
    {
        using var factory = new CatalogIngestionApiFactory();
        using var client = factory.CreateClient();
        Authenticate(client, "catalog.publish", includeSubject: true);
        var command = CreateCommand();
        client.DefaultRequestHeaders.Add("Idempotency-Key", command.CommandId.ToString("D"));

        var response = await client.PostAsJsonAsync(
            "/api/catalog-command/ingestion/drafts",
            command,
            JsonOptions);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, factory.Store.MutationCount);
    }

    [Fact]
    public async Task WorkloadWithoutSubjectFailsClosed()
    {
        using var factory = new CatalogIngestionApiFactory();
        using var client = factory.CreateClient();
        Authenticate(
            client,
            CatalogIngestionAuthorizationPolicies.ExecuteDraftCommand,
            includeSubject: false);
        var command = CreateCommand();
        client.DefaultRequestHeaders.Add("Idempotency-Key", command.CommandId.ToString("D"));

        var response = await client.PostAsJsonAsync(
            "/api/catalog-command/ingestion/drafts",
            command,
            JsonOptions);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("CATALOG_INGESTION_CALLER_REQUIRED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, factory.Store.MutationCount);
    }

    [Fact]
    public async Task IdempotencyKeyMustEqualExactCommandIdentity()
    {
        using var factory = new CatalogIngestionApiFactory();
        using var client = factory.CreateClient();
        Authenticate(
            client,
            CatalogIngestionAuthorizationPolicies.ExecuteDraftCommand,
            includeSubject: true);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.CreateVersion7().ToString("D"));

        var response = await client.PostAsJsonAsync(
            "/api/catalog-command/ingestion/drafts",
            CreateCommand(),
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("CATALOG_INGESTION_IDEMPOTENCY_KEY_INVALID", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, factory.Store.MutationCount);
    }

    [Fact]
    public async Task ExactCommandReplayReturnsSameDraftOutcomeWithoutSecondMutation()
    {
        using var factory = new CatalogIngestionApiFactory();
        using var client = factory.CreateClient();
        Authenticate(
            client,
            CatalogIngestionAuthorizationPolicies.ExecuteDraftCommand,
            includeSubject: true);
        var command = CreateCommand();
        client.DefaultRequestHeaders.Add("Idempotency-Key", command.CommandId.ToString("D"));

        var firstResponse = await client.PostAsJsonAsync(
            "/api/catalog-command/ingestion/drafts",
            command,
            JsonOptions);
        var replayResponse = await client.PostAsJsonAsync(
            "/api/catalog-command/ingestion/drafts",
            command,
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<CatalogIngestionCommandOutcome>(JsonOptions);
        var replay = await replayResponse.Content.ReadFromJsonAsync<CatalogIngestionCommandOutcome>(JsonOptions);
        Assert.NotNull(first);
        Assert.NotNull(replay);
        Assert.Equal(first, replay);
        Assert.Equal(1, factory.Store.MutationCount);
        Assert.Equal(CatalogIngestionOutcomeStateContract.DraftCreated, first.State);
    }

    private static CatalogIngestionUpsertDraftCommand CreateCommand()
    {
        var input = new CatalogIngestionCommandDigestInput(
            Guid.Parse("019ba100-0000-7000-8000-000000000101"),
            Guid.Parse("019ba100-0000-7000-8000-000000000102"),
            "provider-one",
            "berlin",
            "berlin",
            Guid.Parse("019ba100-0000-7000-8000-000000000103"),
            "provider",
            "provider:one",
            [
                new CatalogDraftFieldValueContract(
                    "name",
                    CatalogDraftValueKindContract.Text,
                    "Provider One",
                    "en",
                    "source-one",
                    new string('a', 64),
                    "public_allowed"),
            ],
            Now);
        return new CatalogIngestionUpsertDraftCommand(
            input.CommandId,
            input.IngestionBatchId,
            input.IngestionItemKey,
            CatalogIngestionCommandDigest.Compute(input),
            input.SiteKey,
            input.CatalogKey,
            input.ExpectedCatalogConfigurationRevisionId,
            input.EntityKind,
            input.SubjectNaturalKey,
            input.Fields,
            input.RequestedAtUtc,
            "ingestion:batch:command");
    }

    private static void Authenticate(HttpClient client, string scope, bool includeSubject)
    {
        client.DefaultRequestHeaders.Add(CatalogIngestionApiFactory.AuthenticationHeader, "1");
        client.DefaultRequestHeaders.Add(CatalogIngestionApiFactory.ScopesHeader, scope);
        if (includeSubject)
        {
            client.DefaultRequestHeaders.Add(
                CatalogIngestionApiFactory.SubjectHeader,
                "ingestion-worker");
        }
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
