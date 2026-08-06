using System.Security.Claims;
using System.Text.Encodings.Web;
using Aggregator.Catalog.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Catalog.Api.Tests;

public sealed class CatalogApiFactory : WebApplicationFactory<Program>
{
    public const string AuthenticationHeader = "X-Test-Authentication";
    public const string ActorHeader = "X-Test-Actor";
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

    public CatalogApiFactory()
    {
        foreach (var setting in RequiredEnvironment)
        {
            _originalEnvironment[setting.Key] = Environment.GetEnvironmentVariable(setting.Key);
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
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

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationSchemeName = "CatalogApiTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(AuthenticationHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "test-subject"),
            };
            if (Request.Headers.TryGetValue(ActorHeader, out var actorId))
            {
                claims.Add(new Claim("actor_id", actorId.ToString()));
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
