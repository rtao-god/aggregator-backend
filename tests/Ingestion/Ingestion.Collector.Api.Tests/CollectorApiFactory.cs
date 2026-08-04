using System.Security.Claims;
using System.Text.Encodings.Web;
using Aggregator.Ingestion.Collector.Application;
using Aggregator.Ingestion.Collector.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ingestion.Collector.Api.Tests;

public sealed class CollectorApiFactory : WebApplicationFactory<Program>
{
    public const string AuthenticationHeader = "X-Test-Authentication";
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

    public CollectorApiFactory()
    {
        foreach (var setting in RequiredEnvironment)
        {
            _originalEnvironment[setting.Key] = Environment.GetEnvironmentVariable(setting.Key);
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }

    public RecordingCollectorStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICollectorCandidateStore>();
            services.AddSingleton<ICollectorCandidateStore>(Store);
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

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public new const string Scheme = "CollectorApiTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(AuthenticationHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "collector-test"),
            };
            if (Request.Headers.TryGetValue(ScopesHeader, out var scopes))
            {
                claims.Add(new Claim("scope", scopes.ToString()));
            }

            var principal = new ClaimsPrincipal(
                new ClaimsIdentity(claims, Scheme));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme)));
        }
    }
}

public sealed class RecordingCollectorStore : ICollectorCandidateStore
{
    public CollectorCandidate? Candidate { get; private set; }

    public Task<CollectorCandidateRegistration> RegisterAsync(
        Guid commandId,
        string commandDigest,
        CollectorCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.NotEqual(Guid.Empty, commandId);
        Assert.Equal(64, commandDigest.Length);
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        return Task.FromResult(
            new CollectorCandidateRegistration(candidate, Replayed: false));
    }

    public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}
