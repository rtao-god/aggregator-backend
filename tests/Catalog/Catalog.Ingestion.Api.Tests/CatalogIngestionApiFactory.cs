using System.Security.Claims;
using System.Text.Encodings.Web;
using Aggregator.Catalog.Api;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Catalog.Ingestion.Api.Tests;

public sealed class CatalogIngestionApiFactory : WebApplicationFactory<Program>
{
    public const string AuthenticationHeader = "X-Test-Authentication";
    public const string SubjectHeader = "X-Test-Subject";
    public const string ScopesHeader = "X-Test-Scopes";

    private static readonly IReadOnlyDictionary<string, string> RequiredEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConnectionStrings__Catalog"] =
                "Host=127.0.0.1;Port=1;Database=catalog;Username=test;Password=test;Timeout=1;Command Timeout=1",
            ["Catalog__ObjectStorage__ServiceUrl"] = "http://127.0.0.1:1",
            ["Catalog__ObjectStorage__BucketName"] = "catalog-test",
            ["Catalog__ObjectStorage__AccessKey"] = "test-access",
            ["Catalog__ObjectStorage__SecretKey"] = "test-secret",
            ["CatalogMedia__ObjectStorage__ServiceUrl"] = "http://127.0.0.1:1",
            ["CatalogMedia__ObjectStorage__Region"] = "us-east-1",
            ["CatalogMedia__ObjectStorage__Bucket"] = "catalog-media-test",
            ["CatalogMedia__ObjectStorage__AccessKey"] = "test-access",
            ["CatalogMedia__ObjectStorage__SecretKey"] = "test-secret",
            ["CatalogMedia__ObjectStorage__ForcePathStyle"] = "true",
            ["Authentication__Authority"] = "https://issuer.test",
            ["Authentication__RequireHttpsMetadata"] = "false",
        };

    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);

    public CatalogIngestionApiFactory()
    {
        Store = new TestStore();
        foreach (var setting in RequiredEnvironment)
        {
            _originalEnvironment[setting.Key] = Environment.GetEnvironmentVariable(setting.Key);
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }

    public TestStore Store { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICatalogIngestionDraftStore>();
            services.RemoveAll<ICatalogIngestionDraftCommandHandler>();
            services.AddSingleton<ICatalogIngestionDraftStore>(Store);
            services.AddSingleton<ICatalogIngestionDraftCommandHandler>(
                provider => new TestCommandHandler(
                    new CatalogIngestionDraftService(provider.GetRequiredService<ICatalogIngestionDraftStore>())));
            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.Scheme;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.Scheme;
                    options.DefaultForbidScheme = TestAuthenticationHandler.Scheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.Scheme,
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

    public sealed class TestStore : ICatalogIngestionDraftStore
    {
        private readonly Dictionary<Guid, CatalogIngestionCommandOutcome> _results = [];

        public int MutationCount { get; private set; }

        public Task<CatalogIngestionCommandOutcome> UpsertAsync(
            CatalogIngestionUpsertDraftCommand command,
            string callerIdentity,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            cancellationToken.ThrowIfCancellationRequested();
            if (_results.TryGetValue(command.CommandId, out var existing))
            {
                return Task.FromResult(existing);
            }

            MutationCount++;
            var outcome = new CatalogIngestionCommandOutcome(
                command.CommandId,
                command.IngestionBatchId,
                command.IngestionItemKey,
                CatalogIngestionOutcomeStateContract.DraftCreated,
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                FailureCode: null,
                FailureDetail: null,
                command.RequestedAtUtc);
            _results.Add(command.CommandId, outcome);
            return Task.FromResult(outcome);
        }
    }

    private sealed class TestCommandHandler(CatalogIngestionDraftService service)
        : ICatalogIngestionDraftCommandHandler
    {
        public Task<CatalogIngestionCommandOutcome> ExecuteAsync(
            CatalogIngestionUpsertDraftCommand command,
            string callerIdentity,
            CancellationToken cancellationToken) =>
            service.ExecuteAsync(command, callerIdentity, cancellationToken);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public new const string Scheme = "CatalogIngestionApiTest";

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

            var identity = new ClaimsIdentity(claims, Scheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
        }
    }
}
