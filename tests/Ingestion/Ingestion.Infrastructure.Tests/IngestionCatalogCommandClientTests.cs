using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Infrastructure;

namespace Ingestion.Infrastructure.Tests;

public sealed class IngestionCatalogCommandClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 11, 0, 0, TimeSpan.Zero);
    private static readonly Guid CommandId = Guid.Parse("0198b200-0000-7000-8000-000000000001");
    private static readonly Guid BatchId = Guid.Parse("0198b200-0000-7000-8000-000000000002");
    private static readonly Guid ListingId = Guid.Parse("0198b200-0000-7000-8000-000000000003");
    private static readonly Guid ListingRevisionId = Guid.Parse("0198b200-0000-7000-8000-000000000004");

    [Fact]
    public async Task SendsExactAuthenticatedCommandAndCachesWorkloadToken()
    {
        var tokenHandler = new CapturingHandler(_ => TokenResponse());
        var commandHandler = new CapturingHandler(_ => OutcomeResponse(CreateOutcome()));
        using var clients = new NamedClientFactory(commandHandler, tokenHandler);
        var options = CreateOptions();
        var time = new FixedTimeProvider(Now);
        var tokenProvider = new IngestionCatalogAccessTokenProvider(clients, options, time);
        var client = new IngestionCatalogCommandClient(clients, tokenProvider, time);
        var command = CreateCommand();

        _ = await client.SendAsync(command, CancellationToken.None);
        _ = await client.SendAsync(command, CancellationToken.None);

        Assert.Single(tokenHandler.Requests);
        Assert.Equal(2, commandHandler.Requests.Count);
        var tokenRequest = tokenHandler.Requests[0];
        Assert.Equal(HttpMethod.Post, tokenRequest.Method);
        Assert.Equal(options.TokenEndpoint, tokenRequest.Uri);
        Assert.Contains("grant_type=client_credentials", tokenRequest.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=ingestion-worker", tokenRequest.Body, StringComparison.Ordinal);
        Assert.Contains("scope=catalog.ingestion", tokenRequest.Body, StringComparison.Ordinal);

        var catalogRequest = commandHandler.Requests[0];
        Assert.Equal(HttpMethod.Post, catalogRequest.Method);
        Assert.Equal(
            new Uri(options.BaseAddress, "api/catalog-command/ingestion/drafts"),
            catalogRequest.Uri);
        Assert.Equal("Bearer", catalogRequest.Authorization?.Scheme);
        Assert.Equal("workload-token", catalogRequest.Authorization?.Parameter);
        Assert.Equal(command.CommandId.ToString("D"), catalogRequest.IdempotencyKey);
        Assert.Equal(command.CorrelationId, catalogRequest.CorrelationId);
        var sentCommand = JsonSerializer.Deserialize<CatalogIngestionUpsertDraftCommand>(
            catalogRequest.Body,
            WireOptions);
        Assert.NotNull(sentCommand);
        Assert.Equal(command.CommandId, sentCommand.CommandId);
        Assert.Equal(command.IngestionBatchId, sentCommand.IngestionBatchId);
        Assert.Equal(command.IngestionItemKey, sentCommand.IngestionItemKey);
        Assert.Equal(command.CommandDigest, sentCommand.CommandDigest);
        Assert.Equal(command.CorrelationId, sentCommand.CorrelationId);
        var sentField = Assert.Single(sentCommand.Fields);
        var expectedField = Assert.Single(command.Fields);
        Assert.Equal(expectedField, sentField);
    }

    [Fact]
    public async Task RejectsCatalogOutcomeForDifferentCommandIdentity()
    {
        var tokenHandler = new CapturingHandler(_ => TokenResponse());
        var mismatched = CreateOutcome() with { IngestionItemKey = "another-item" };
        var commandHandler = new CapturingHandler(_ => OutcomeResponse(mismatched));
        using var clients = new NamedClientFactory(commandHandler, tokenHandler);
        var options = CreateOptions();
        var time = new FixedTimeProvider(Now);
        var client = new IngestionCatalogCommandClient(
            clients,
            new IngestionCatalogAccessTokenProvider(clients, options, time),
            time);

        var exception = await Assert.ThrowsAsync<IngestionCatalogCommandTransportException>(() =>
            client.SendAsync(CreateCommand(), CancellationToken.None));

        Assert.Equal("INGESTION_CATALOG_OUTCOME_IDENTITY_MISMATCH", exception.Code);
        Assert.False(exception.IsTransient);
    }

    [Fact]
    public async Task RejectsUnmappedCatalogOutcomeMember()
    {
        var tokenHandler = new CapturingHandler(_ => TokenResponse());
        var document = JsonSerializer.SerializeToNode(CreateOutcome(), WireOptions)!.AsObject();
        document["unexpected"] = true;
        var commandHandler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(document.ToJsonString(), Encoding.UTF8, "application/json"),
        });
        using var clients = new NamedClientFactory(commandHandler, tokenHandler);
        var options = CreateOptions();
        var time = new FixedTimeProvider(Now);
        var client = new IngestionCatalogCommandClient(
            clients,
            new IngestionCatalogAccessTokenProvider(clients, options, time),
            time);

        var exception = await Assert.ThrowsAsync<IngestionCatalogCommandTransportException>(() =>
            client.SendAsync(CreateCommand(), CancellationToken.None));

        Assert.Equal("INGESTION_CATALOG_OUTCOME_JSON_INVALID", exception.Code);
    }

    [Fact]
    public void FailureClassifierHonorsRetryAfterAndAttemptLimit()
    {
        var classifier = new IngestionCatalogDeliveryFailureClassifier();
        var transient = new IngestionCatalogCommandTransportException(
            "CATALOG_BUSY",
            HttpStatusCode.TooManyRequests,
            "Catalog is busy.",
            TimeSpan.FromSeconds(45));

        var retry = classifier.Classify(transient, 2, 8, Now);
        var terminal = classifier.Classify(transient, 8, 8, Now);

        Assert.True(retry.Retry);
        Assert.Equal(Now.AddSeconds(45), retry.NextAttemptAtUtc);
        Assert.Equal("CATALOG_BUSY", retry.FailureCode);
        Assert.False(terminal.Retry);
        Assert.Null(terminal.NextAttemptAtUtc);
        Assert.Equal("INGESTION_CATALOG_DELIVERY_RETRY_EXHAUSTED", terminal.FailureCode);
    }

    private static IngestionCatalogCommandClientOptions CreateOptions() =>
        new()
        {
            BaseAddress = new Uri("https://catalog.internal/", UriKind.Absolute),
            TokenEndpoint = new Uri("https://identity.internal/token", UriKind.Absolute),
            ClientId = "ingestion-worker",
            ClientSecret = "not-a-placeholder-secret",
            Scope = "catalog.ingestion",
            RequestTimeout = TimeSpan.FromSeconds(30),
            RefreshSkew = TimeSpan.FromSeconds(30),
            AllowInsecureHttp = false,
        };

    private static CatalogIngestionUpsertDraftCommand CreateCommand()
    {
        var fields = new[]
        {
            new CatalogDraftFieldValueContract(
                "name",
                CatalogDraftValueKindContract.Text,
                "Example Provider",
                "en-GB",
                "collector",
                new string('a', 64),
                "public"),
        };
        var input = new CatalogIngestionCommandDigestInput(
            CommandId,
            BatchId,
            "item-001",
            "site",
            "catalog",
            Guid.Parse("0198b200-0000-7000-8000-000000000005"),
            "provider",
            "provider:example",
            fields,
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
            "collector-run-0001");
    }

    private static CatalogIngestionCommandOutcome CreateOutcome() =>
        new(
            CommandId,
            BatchId,
            "item-001",
            CatalogIngestionOutcomeStateContract.DraftCreated,
            ListingId,
            ListingRevisionId,
            null,
            null,
            Now);

    private static HttpResponseMessage TokenResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"access_token":"workload-token","token_type":"Bearer","expires_in":3600}
                """,
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage OutcomeResponse(CatalogIngestionCommandOutcome outcome) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(outcome, WireOptions),
                Encoding.UTF8,
                "application/json"),
        };

    private static JsonSerializerOptions WireOptions { get; } = CreateWireOptions();

    private static JsonSerializerOptions CreateWireOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class NamedClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _commandClient;
        private readonly HttpClient _tokenClient;

        public NamedClientFactory(
            HttpMessageHandler commandHandler,
            HttpMessageHandler tokenHandler)
        {
            _commandClient = new HttpClient(commandHandler)
            {
                BaseAddress = new Uri("https://catalog.internal/", UriKind.Absolute),
            };
            _tokenClient = new HttpClient(tokenHandler);
        }

        public HttpClient CreateClient(string name) => name switch
        {
            "ingestion-catalog-command" => _commandClient,
            "ingestion-catalog-token" => _tokenClient,
            _ => throw new InvalidOperationException($"Unexpected HTTP client '{name}'."),
        };

        public void Dispose()
        {
            _commandClient.Dispose();
            _tokenClient.Dispose();
        }
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            request.Headers.TryGetValues("Idempotency-Key", out var idempotencyValues);
            request.Headers.TryGetValues("X-Correlation-Id", out var correlationValues);
            Requests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization,
                idempotencyValues?.SingleOrDefault(),
                correlationValues?.SingleOrDefault(),
                body));
            return responseFactory(request);
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        AuthenticationHeaderValue? Authorization,
        string? IdempotencyKey,
        string? CorrelationId,
        string Body);
}
