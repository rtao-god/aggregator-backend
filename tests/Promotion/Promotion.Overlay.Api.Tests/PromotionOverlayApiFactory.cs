using System.Security.Claims;
using System.Text.Encodings.Web;
using Aggregator.Promotion.Overlay.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Promotion.Overlay.Api.Tests;

public sealed class PromotionOverlayApiFactory : WebApplicationFactory<Program>
{
    public const string AuthenticationHeader = "X-Test-Authentication";
    public const string ScopesHeader = "X-Test-Scopes";

    private static readonly IReadOnlyDictionary<string, string> RequiredEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConnectionStrings__Promotion"] =
                "Host=127.0.0.1;Port=1;Database=promotion;Username=test;Password=test;Timeout=1;Command Timeout=1",
            ["Authentication__Authority"] = "https://issuer.test",
            ["Authentication__RequireHttpsMetadata"] = "false",
        };

    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);

    public PromotionOverlayApiFactory()
    {
        foreach (var setting in RequiredEnvironment)
        {
            _originalEnvironment[setting.Key] = Environment.GetEnvironmentVariable(setting.Key);
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }

    public RecordingPromotionOverlayStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPromotionOverlayStore>();
            services.AddSingleton<IPromotionOverlayStore>(Store);
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
        public new const string Scheme = "PromotionOverlayApiTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(AuthenticationHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "promotion-test"),
            };
            if (Request.Headers.TryGetValue(ScopesHeader, out var scopes))
            {
                claims.Add(new Claim("scope", scopes.ToString()));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme)));
        }
    }
}

public sealed class RecordingPromotionOverlayStore : IPromotionOverlayStore
{
    public PromotionOverlayPublication? Publication { get; private set; }

    public Task<long> GetNextActivationRevisionAsync(
        string catalogKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal("berlin-recording-services", catalogKey);
        return Task.FromResult(1L);
    }

    public Task<PromotionOverlayCommitResult> CommitAsync(
        PromotionOverlayPublication publication,
        Guid? expectedCurrentOverlayId,
        string commandDigest,
        PromotionOverlayOutboxMessage outboxMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(commandDigest);
        ArgumentNullException.ThrowIfNull(outboxMessage);
        Assert.Null(expectedCurrentOverlayId);
        Assert.Equal(64, commandDigest.Length);
        Assert.Equal(64, outboxMessage.PayloadDigest.Length);
        Publication = publication;
        return Task.FromResult(new PromotionOverlayCommitResult(publication, Replayed: false));
    }
}
