using System.Security.Claims;
using System.Text.Encodings.Web;
using Aggregator.Ingestion.Api;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ingestion.Api.Tests;

public sealed class IngestionApiFactory : WebApplicationFactory<Program>
{
    public const string AuthenticationHeader = "X-Test-Authentication";
    public const string SubjectHeader = "X-Test-Subject";
    public const string ScopesHeader = "X-Test-Scopes";

    private static readonly IReadOnlyDictionary<string, string> RequiredEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConnectionStrings__Ingestion"] =
                "Host=127.0.0.1;Port=1;Database=ingestion;Username=test;Password=test;Timeout=1;Command Timeout=1",
            ["Authentication__Authority"] = "https://issuer.test",
            ["Authentication__RequireHttpsMetadata"] = "false",
        };

    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);

    public IngestionApiFactory()
    {
        Backend = new TestIngestionBackend();
        foreach (var setting in RequiredEnvironment)
        {
            _originalEnvironment[setting.Key] = Environment.GetEnvironmentVariable(setting.Key);
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }

    public TestIngestionBackend Backend { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IIngestionBatchRepository>();
            services.RemoveAll<IIngestionProducerRegistry>();
            services.RemoveAll<ICatalogIngestionReferenceReader>();
            services.RemoveAll<IIngestionClock>();
            services.RemoveAll<IIngestionIdSource>();
            services.AddSingleton<IIngestionBatchRepository>(Backend);
            services.AddSingleton<IIngestionProducerRegistry>(Backend);
            services.AddSingleton<ICatalogIngestionReferenceReader>(Backend);
            services.AddSingleton<IIngestionClock>(Backend);
            services.AddSingleton<IIngestionIdSource>(Backend);
            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.AuthenticationSchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.AuthenticationSchemeName;
                    options.DefaultForbidScheme = TestAuthenticationHandler.AuthenticationSchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.AuthenticationSchemeName,
                    _ => { });
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var setting in _originalEnvironment)
            {
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            }
        }

        base.Dispose(disposing);
    }

    public sealed class TestIngestionBackend :
        IIngestionBatchRepository,
        IIngestionProducerRegistry,
        ICatalogIngestionReferenceReader,
        IIngestionClock,
        IIngestionIdSource
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, IngestionBatchSnapshot> _batches = [];
        private readonly Dictionary<(string Scope, string Key), (string Digest, Guid BatchId)> _commands = [];
        private readonly Dictionary<(string Producer, Guid CollectorExportId), Guid> _exports = [];

        public Guid ActiveConfigurationRevisionId { get; } =
            Guid.Parse("0198a123-0000-7000-8000-000000000301");

        public DateTimeOffset UtcNow { get; } =
            new(2026, 8, 4, 6, 15, 0, TimeSpan.Zero);

        public string? LastCallerServiceIdentity { get; private set; }

        public Task<IngestionBatchRegistrationResult> RegisterAsync(
            ImportBatch batch,
            AggregatorCandidateIngestionManifest manifest,
            IngestionCommandIdentity commandIdentity,
            string callerServiceIdentity,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(batch);
            ArgumentNullException.ThrowIfNull(manifest);
            ArgumentNullException.ThrowIfNull(commandIdentity);
            lock (_gate)
            {
                var commandKey = (commandIdentity.Scope, commandIdentity.Key);
                if (_commands.TryGetValue(commandKey, out var existingCommand))
                {
                    if (!string.Equals(
                            existingCommand.Digest,
                            commandIdentity.RequestDigest,
                            StringComparison.Ordinal))
                    {
                        throw new IngestionApplicationException(
                            "Ingestion.Commands",
                            "INGESTION_IDEMPOTENCY_DIGEST_CONFLICT",
                            409,
                            "The Idempotency-Key was already used for a different registration request.",
                            "Reuse the key only with the exact original request or submit a new stable key.");
                    }

                    return Task.FromResult(
                        new IngestionBatchRegistrationResult(
                            _batches[existingCommand.BatchId],
                            true));
                }

                var exportKey = (batch.ProducerIdentity, batch.CollectorExportId);
                if (_exports.TryGetValue(exportKey, out var existingBatchId))
                {
                    throw new IngestionApplicationException(
                        "Ingestion.Batches",
                        "INGESTION_COLLECTOR_EXPORT_ALREADY_REGISTERED",
                        409,
                        "The producer and collector export identity is already registered.",
                        "Read the existing import batch instead of registering it again.",
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["existingBatchId"] = existingBatchId,
                        });
                }

                var snapshot = IngestionBatchSnapshot.From(batch);
                _batches.Add(snapshot.Id.Value, snapshot);
                _commands.Add(commandKey, (commandIdentity.RequestDigest, snapshot.Id.Value));
                _exports.Add(exportKey, snapshot.Id.Value);
                LastCallerServiceIdentity = callerServiceIdentity;
                return Task.FromResult(new IngestionBatchRegistrationResult(snapshot, false));
            }
        }

        public Task<IngestionBatchSnapshot?> ReadAsync(
            ImportBatchId batchId,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(
                    _batches.TryGetValue(batchId.Value, out var batch)
                        ? batch
                        : null);
            }
        }

        public Task<RegisteredIngestionProducer?> GetAsync(
            string producerIdentity,
            CancellationToken cancellationToken)
        {
            RegisteredIngestionProducer? producer = string.Equals(
                producerIdentity,
                "collector-berlin",
                StringComparison.Ordinal)
                ? new RegisteredIngestionProducer(
                    producerIdentity,
                    Active: true,
                    [AggregatorCandidateIngestionContract.Revision])
                : null;
            return Task.FromResult(producer);
        }

        Task<CatalogIngestionReference?> ICatalogIngestionReferenceReader.GetAsync(
            string siteKey,
            string catalogKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<CatalogIngestionReference?>(
                new CatalogIngestionReference(
                    siteKey,
                    catalogKey,
                    ActiveConfigurationRevisionId,
                    [IngestionEntityKindContract.Place, IngestionEntityKindContract.Provider],
                    AggregateRevision: 1));

        public DateTimeOffset GetUtcNow() => UtcNow;

        public Guid CreateId() => Guid.CreateVersion7();
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationSchemeName = "IngestionApiTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(AuthenticationHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>();
            if (Request.Headers.TryGetValue(SubjectHeader, out var subject))
            {
                claims.Add(new Claim("sub", subject.ToString()));
            }

            if (Request.Headers.TryGetValue(ScopesHeader, out var scopes))
            {
                claims.Add(new Claim("scope", scopes.ToString()));
            }

            var identity = new ClaimsIdentity(claims, AuthenticationSchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, AuthenticationSchemeName)));
        }
    }
}
