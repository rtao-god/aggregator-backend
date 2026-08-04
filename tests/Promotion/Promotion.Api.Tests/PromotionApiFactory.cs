using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Aggregator.Promotion.Api;
using Aggregator.Promotion.Application;
using Aggregator.Promotion.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Promotion.Api.Tests;

public sealed class PromotionApiFactory : WebApplicationFactory<Program>
{
    public const string AuthenticationHeader = "X-Test-Authentication";
    public const string ActorHeader = "X-Test-Actor";
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

    public PromotionApiFactory()
    {
        Backend = new TestPromotionBackend();
        foreach (var setting in RequiredEnvironment)
        {
            _originalEnvironment[setting.Key] = Environment.GetEnvironmentVariable(setting.Key);
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }

    public TestPromotionBackend Backend { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPromotionRepository>();
            services.RemoveAll<IPromotionClock>();
            services.RemoveAll<IPromotionIdSource>();
            services.AddSingleton<IPromotionRepository>(Backend);
            services.AddSingleton<IPromotionClock>(Backend);
            services.AddSingleton<IPromotionIdSource>(Backend);
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

    public sealed class TestPromotionBackend :
        IPromotionRepository,
        IPromotionClock,
        IPromotionIdSource
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string Scope, string Key), StoredCommand> _commands = [];
        private readonly Dictionary<Guid, PromotionProduct> _products = [];
        private readonly Dictionary<Guid, PromotionEntitlement> _entitlements = [];
        private readonly Dictionary<Guid, SponsoredPlacement> _placements = [];
        private readonly Dictionary<(string CatalogKey, Guid ListingId), ListingPromotionEligibility> _eligibility = [];

        public DateTimeOffset UtcNow { get; } =
            new(2026, 8, 4, 8, 0, 0, TimeSpan.Zero);

        public int ProductCount
        {
            get
            {
                lock (_gate)
                {
                    return _products.Count;
                }
            }
        }

        public Task<PromotionProduct?> GetProductAsync(
            Guid productId,
            CancellationToken cancellationToken) =>
            ReadAsync(_products, productId, cancellationToken);

        public Task<PromotionProduct?> GetProductByKeyAsync(
            string productKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_products.Values.SingleOrDefault(product =>
                    string.Equals(product.Key, productKey, StringComparison.Ordinal)));
            }
        }

        public Task<PromotionEntitlement?> GetEntitlementAsync(
            Guid entitlementId,
            CancellationToken cancellationToken) =>
            ReadAsync(_entitlements, entitlementId, cancellationToken);

        public Task<SponsoredPlacement?> GetPlacementAsync(
            Guid placementId,
            CancellationToken cancellationToken) =>
            ReadAsync(_placements, placementId, cancellationToken);

        public Task<ListingPromotionEligibility?> GetEligibilityAsync(
            string catalogKey,
            Guid listingId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _eligibility.TryGetValue((catalogKey, listingId), out var value);
                return Task.FromResult(value);
            }
        }

        public Task<IReadOnlyList<PromotionEntitlement>> ListEntitlementsAsync(
            Guid listingId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                IReadOnlyList<PromotionEntitlement> result = _entitlements.Values
                    .Where(entitlement => entitlement.ListingId == listingId)
                    .OrderBy(entitlement => entitlement.EffectiveWindow.StartsAtUtc)
                    .ThenBy(entitlement => entitlement.Id)
                    .ToArray();
                return Task.FromResult(result);
            }
        }

        public Task<IReadOnlyList<SponsoredPlacement>> ListPlacementsAsync(
            string catalogKey,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestedWindow = PromotionWindow.Create(fromUtc, toUtc);
            lock (_gate)
            {
                IReadOnlyList<SponsoredPlacement> result = _placements.Values
                    .Where(placement => string.Equals(
                        placement.CurrentRevision.CatalogKey,
                        catalogKey,
                        StringComparison.Ordinal))
                    .Where(placement => placement.CurrentRevision.EffectiveWindow.Overlaps(requestedWindow))
                    .OrderBy(placement => placement.Id)
                    .ToArray();
                return Task.FromResult(result);
            }
        }

        public Task<bool> HasPlacementConflictAsync(
            SponsoredPlacement candidate,
            Guid? excludedPlacementId,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_placements.Values.Any(placement =>
                    placement.Id != excludedPlacementId && candidate.Overlaps(placement)));
            }
        }

        public Task<PromotionCommandResult<PromotionProduct>> AddProductAsync(
            PromotionProduct product,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            CancellationToken cancellationToken) =>
            SaveAsync(product, _products, addOnly: true, commandIdentity, commandContext, cancellationToken);

        public Task<PromotionCommandResult<PromotionProduct>> SaveProductAsync(
            PromotionProduct product,
            long expectedStoredAggregateRevision,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            CancellationToken cancellationToken)
        {
            _ = expectedStoredAggregateRevision;
            return SaveAsync(product, _products, addOnly: false, commandIdentity, commandContext, cancellationToken);
        }

        public Task<PromotionCommandResult<PromotionEntitlement>> AddEntitlementAsync(
            PromotionEntitlement entitlement,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            PromotionOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(outboxMessage);
            return SaveAsync(
                entitlement,
                _entitlements,
                addOnly: true,
                commandIdentity,
                commandContext,
                cancellationToken);
        }

        public Task<PromotionCommandResult<PromotionEntitlement>> SaveEntitlementAsync(
            PromotionEntitlement entitlement,
            long expectedStoredAggregateRevision,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            PromotionOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            _ = expectedStoredAggregateRevision;
            ArgumentNullException.ThrowIfNull(outboxMessage);
            return SaveAsync(
                entitlement,
                _entitlements,
                addOnly: false,
                commandIdentity,
                commandContext,
                cancellationToken);
        }

        public Task<PromotionCommandResult<SponsoredPlacement>> AddPlacementAsync(
            SponsoredPlacement placement,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            PromotionOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(outboxMessage);
            return SaveAsync(
                placement,
                _placements,
                addOnly: true,
                commandIdentity,
                commandContext,
                cancellationToken);
        }

        public Task<PromotionCommandResult<SponsoredPlacement>> SavePlacementAsync(
            SponsoredPlacement placement,
            long expectedStoredAggregateRevision,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            PromotionOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            _ = expectedStoredAggregateRevision;
            ArgumentNullException.ThrowIfNull(outboxMessage);
            return SaveAsync(
                placement,
                _placements,
                addOnly: false,
                commandIdentity,
                commandContext,
                cancellationToken);
        }

        public Task UpsertEligibilityAsync(
            ListingPromotionEligibility eligibility,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(eligibility);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _eligibility[(eligibility.CatalogKey, eligibility.ListingId)] = eligibility;
            }

            return Task.CompletedTask;
        }

        public DateTimeOffset GetUtcNow() => UtcNow;

        public Guid CreateId() => Guid.CreateVersion7();

        private Task<TAggregate?> ReadAsync<TAggregate>(
            IReadOnlyDictionary<Guid, TAggregate> values,
            Guid id,
            CancellationToken cancellationToken)
            where TAggregate : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                values.TryGetValue(id, out var value);
                return Task.FromResult(value);
            }
        }

        private Task<PromotionCommandResult<TAggregate>> SaveAsync<TAggregate>(
            TAggregate aggregate,
            IDictionary<Guid, TAggregate> values,
            bool addOnly,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            CancellationToken cancellationToken)
            where TAggregate : class
        {
            ArgumentNullException.ThrowIfNull(aggregate);
            ArgumentNullException.ThrowIfNull(commandIdentity);
            ArgumentNullException.ThrowIfNull(commandContext);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (TryReplay(commandIdentity, out TAggregate? replay))
                {
                    return Task.FromResult(new PromotionCommandResult<TAggregate>(replay, true));
                }

                var id = aggregate switch
                {
                    PromotionProduct product => product.Id,
                    PromotionEntitlement entitlement => entitlement.Id,
                    SponsoredPlacement placement => placement.Id,
                    _ => throw new InvalidOperationException("Unsupported Promotion test aggregate."),
                };
                if (addOnly)
                {
                    if (!values.TryAdd(id, aggregate))
                    {
                        throw new InvalidOperationException("Promotion test aggregate identity already exists.");
                    }
                }
                else
                {
                    values[id] = aggregate;
                }

                _commands.Add(
                    (commandIdentity.Scope, commandIdentity.Key),
                    new StoredCommand(commandIdentity.RequestDigest, aggregate));
                return Task.FromResult(new PromotionCommandResult<TAggregate>(aggregate, false));
            }
        }

        private bool TryReplay<TAggregate>(
            PromotionCommandIdentity identity,
            [NotNullWhen(true)] out TAggregate? aggregate)
            where TAggregate : class
        {
            if (!_commands.TryGetValue((identity.Scope, identity.Key), out var existing))
            {
                aggregate = null;
                return false;
            }

            if (!string.Equals(existing.RequestDigest, identity.RequestDigest, StringComparison.Ordinal))
            {
                throw new PromotionCampaignApplicationException(
                    "Promotion.Commands",
                    "PROMOTION_IDEMPOTENCY_CONFLICT",
                    409,
                    "Promotion command key was already used with another request digest.",
                    "Use the original request or submit a new semantic idempotency key.");
            }

            aggregate = existing.Aggregate as TAggregate
                ?? throw new InvalidOperationException("Promotion command result type is inconsistent.");
            return true;
        }

        private sealed record StoredCommand(string RequestDigest, object Aggregate);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationSchemeName = "PromotionApiTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(AuthenticationHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "promotion-test-subject"),
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
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationSchemeName)));
        }
    }
}
