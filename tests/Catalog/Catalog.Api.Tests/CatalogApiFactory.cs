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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.UseSetting(
            "ConnectionStrings:Catalog",
            "Host=127.0.0.1;Port=1;Database=catalog;Username=test;Password=test;Timeout=1;Command Timeout=1");
        builder.UseSetting("Catalog:ObjectStorage:ServiceUrl", "http://127.0.0.1:1");
        builder.UseSetting("Catalog:ObjectStorage:BucketName", "catalog-test");
        builder.UseSetting("Catalog:ObjectStorage:AccessKey", "test-access");
        builder.UseSetting("Catalog:ObjectStorage:SecretKey", "test-secret");
        builder.UseSetting("Authentication:Authority", "https://issuer.test");
        builder.UseSetting("Authentication:RequireHttpsMetadata", "false");
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
