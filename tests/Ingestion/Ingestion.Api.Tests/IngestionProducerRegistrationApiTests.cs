using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Ingestion.Api;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ingestion.Api.Tests;

public sealed class IngestionProducerRegistrationApiTests
    : IClassFixture<IngestionProducerRegistrationApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly IngestionProducerRegistrationApiFactory _factory;

    public IngestionProducerRegistrationApiTests(
        IngestionProducerRegistrationApiFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task PrivilegedCallerCanCreateReadAndReplayRegistration()
    {
        using var client = _factory.CreateClient();
        using var create = CreatePutRequest(
            "producer-registration-api-0001",
            new PutIngestionProducerRegistrationRequest(
                "collector.berlin",
                0,
                true,
                [1],
                "Authorize the Berlin collector workload."));

        using var created = await client.SendAsync(create);
        var createdDocument = await ReadJsonAsync(created);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.False(createdDocument.RootElement.GetProperty("replayed").GetBoolean());
        var registration = createdDocument.RootElement.GetProperty("registration");
        Assert.Equal("collector.berlin", registration.GetProperty("producerIdentity").GetString());
        Assert.True(registration.GetProperty("active").GetBoolean());
        Assert.Equal(1, registration.GetProperty("aggregateRevision").GetInt64());
        Assert.Equal(
            "producer-admin",
            registration.GetProperty("updatedByServiceIdentity").GetString());

        using var read = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/ingestion/producer-registrations?producerIdentity=collector.berlin");
        Authenticate(read, IngestionScopes.ManageProducers);
        using var readResponse = await client.SendAsync(read);
        var readDocument = await ReadJsonAsync(readResponse);
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal(
            registration.GetProperty("contentDigest").GetString(),
            readDocument.RootElement
                .GetProperty("registration")
                .GetProperty("contentDigest")
                .GetString());

        using var replay = CreatePutRequest(
            "producer-registration-api-0001",
            new PutIngestionProducerRegistrationRequest(
                "collector.berlin",
                0,
                true,
                [1],
                "Authorize the Berlin collector workload."));
        using var replayResponse = await client.SendAsync(replay);
        var replayDocument = await ReadJsonAsync(replayResponse);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.True(replayDocument.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(
            1,
            replayDocument.RootElement
                .GetProperty("registration")
                .GetProperty("aggregateRevision")
                .GetInt64());
    }

    [Fact]
    public async Task ReusedIdempotencyKeyWithDifferentRequestReturnsConflict()
    {
        using var client = _factory.CreateClient();
        using var first = CreatePutRequest(
            "producer-registration-api-0002",
            new PutIngestionProducerRegistrationRequest(
                "collector.hamburg",
                0,
                true,
                [1],
                "Authorize the Hamburg collector workload."));
        using var firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        using var conflict = CreatePutRequest(
            "producer-registration-api-0002",
            new PutIngestionProducerRegistrationRequest(
                "collector.hamburg",
                0,
                false,
                [1],
                "Deactivate the Hamburg collector workload."));
        using var conflictResponse = await client.SendAsync(conflict);
        var document = await ReadJsonAsync(conflictResponse);

        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        Assert.Equal(
            "INGESTION_PRODUCER_IDEMPOTENCY_CONFLICT",
            document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ProducerManagementRequiresExactScope()
    {
        using var client = _factory.CreateClient();
        using var request = CreatePutRequest(
            "producer-registration-api-0003",
            new PutIngestionProducerRegistrationRequest(
                "collector.munich",
                0,
                true,
                [1],
                "Authorize the Munich collector workload."),
            scope: IngestionScopes.Submit);

        using var response = await client.SendAsync(request);
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("AUTHORIZATION_DENIED", document.RootElement.GetProperty("code").GetString());
    }

    private static HttpRequestMessage CreatePutRequest(
        string idempotencyKey,
        PutIngestionProducerRegistrationRequest body,
        string scope = IngestionScopes.ManageProducers)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            "/api/ingestion/producer-registrations")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        Authenticate(request, scope);
        return request;
    }

    private static void Authenticate(HttpRequestMessage request, string scope)
    {
        request.Headers.Add(IngestionProducerRegistrationApiFactory.AuthenticationHeader, "true");
        request.Headers.Add(IngestionProducerRegistrationApiFactory.SubjectHeader, "producer-admin");
        request.Headers.Add(IngestionProducerRegistrationApiFactory.ScopesHeader, scope);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return JsonDocument.Parse(bytes);
    }

    private static JsonSerializerOptions CreateJsonOptions()
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
}

public sealed class IngestionProducerRegistrationApiFactory : WebApplicationFactory<Program>
{
    public const string AuthenticationHeader = "X-Test-Authenticated";
    public const string SubjectHeader = "X-Test-Subject";
    public const string ScopesHeader = "X-Test-Scopes";
    private const string Scheme = "ProducerRegistryTest";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ingestion"] =
                    "Host=localhost;Database=ingestion_tests;Username=tests;Password=tests",
                ["Authentication:Authority"] = "https://identity.example.test",
                ["Authentication:RequireHttpsMetadata"] = "false",
                ["Ingestion:ObjectStorage:ServiceUrl"] = "http://127.0.0.1:8333",
                ["Ingestion:ObjectStorage:Region"] = "us-east-1",
                ["Ingestion:ObjectStorage:Bucket"] = "ingestion-tests",
                ["Ingestion:ObjectStorage:AccessKey"] = "tests",
                ["Ingestion:ObjectStorage:SecretKey"] = "tests-secret",
                ["Ingestion:ObjectStorage:ForcePathStyle"] = "true",
                ["Ingestion:ObjectStorage:PresignedUrlLifetimeSeconds"] = "300",
                ["Ingestion:Upload:MinimumPayloadBytes"] = "1",
                ["Ingestion:Upload:MaximumPayloadBytes"] = "1048576",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IIngestionProducerRegistrationStore>();
            services.AddSingleton<IIngestionProducerRegistrationStore, TestProducerRegistrationStore>();
            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = Scheme;
                    options.DefaultChallengeScheme = Scheme;
                    options.DefaultForbidScheme = Scheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    Scheme,
                    _ => { });
        });
    }

    private sealed class TestProducerRegistrationStore : IIngestionProducerRegistrationStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, IngestionProducerRegistrationSnapshot> _registrations =
            new(StringComparer.Ordinal);
        private readonly Dictionary<(string Scope, string Key), StoredCommand> _commands = new();

        public Task<IngestionProducerRegistrationMutationResult> PutAsync(
            IngestionProducerRegistrationMutation mutation,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var commandKey = (mutation.CommandIdentity.Scope, mutation.CommandIdentity.Key);
                if (_commands.TryGetValue(commandKey, out var command))
                {
                    if (!string.Equals(
                            command.RequestDigest,
                            mutation.CommandIdentity.RequestDigest,
                            StringComparison.Ordinal))
                    {
                        throw new IngestionApplicationException(
                            "Ingestion.ProducerRegistry",
                            "INGESTION_PRODUCER_IDEMPOTENCY_CONFLICT",
                            409,
                            "The idempotency key is already bound to another request.",
                            "Use the original request or a new Idempotency-Key.");
                    }

                    return Task.FromResult(new IngestionProducerRegistrationMutationResult(
                        command.Result,
                        Replayed: true));
                }

                _registrations.TryGetValue(
                    mutation.Registration.ProducerIdentity,
                    out var current);
                var actualRevision = current?.AggregateRevision ?? 0;
                if (actualRevision != mutation.ExpectedAggregateRevision)
                {
                    throw new IngestionApplicationException(
                        "Ingestion.ProducerRegistry",
                        "INGESTION_PRODUCER_REVISION_CONFLICT",
                        409,
                        "The producer registration revision changed.",
                        "Read the current revision and retry.");
                }

                _registrations[mutation.Registration.ProducerIdentity] = mutation.Registration;
                _commands[commandKey] = new StoredCommand(
                    mutation.CommandIdentity.RequestDigest,
                    mutation.Registration);
                return Task.FromResult(new IngestionProducerRegistrationMutationResult(
                    mutation.Registration,
                    Replayed: false));
            }
        }

        public Task<IngestionProducerRegistrationSnapshot?> ReadAsync(
            string producerIdentity,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _registrations.TryGetValue(producerIdentity, out var registration);
                return Task.FromResult(registration);
            }
        }

        private sealed record StoredCommand(
            string RequestDigest,
            IngestionProducerRegistrationSnapshot Result);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(AuthenticationHeader, out var authenticated) ||
                !string.Equals(authenticated.ToString(), "true", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var subject = Request.Headers.TryGetValue(SubjectHeader, out var subjectHeader)
                ? subjectHeader.ToString()
                : "producer-admin";
            var claims = new List<Claim> { new("sub", subject) };
            if (Request.Headers.TryGetValue(ScopesHeader, out var scopes))
            {
                claims.AddRange(scopes
                    .ToString()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(scope => new Claim("scope", scope)));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme)));
        }
    }
}
