using System.Globalization;
using Aggregator.Analytics.Api;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Analytics.Api.Tests;

public sealed class AnalyticsApiFactory : WebApplicationFactory<Program>
{
    public const string AuthenticationScheme = "Analytics.Test";

    public const string ActorHeader = "X-Test-Actor-Id";

    public const string ScopesHeader = "X-Test-Scopes";

    private const string AntiAbuseSigningKey =
        "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";

    private static readonly IReadOnlyDictionary<string, string> RequiredEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConnectionStrings__Analytics"] =
                "Host=127.0.0.1;Port=1;Database=analytics;Username=test;Password=test;Timeout=1;Command Timeout=1",
            ["Analytics__AntiAbuseSigningKey"] = AntiAbuseSigningKey,
            ["Authentication__Authority"] = "https://issuer.test",
            ["Authentication__RequireHttpsMetadata"] = "false",
        };

    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);

    public AnalyticsApiFactory()
    {
        Backend = new RecordingAnalyticsBackend();
        Clock = new FixedAnalyticsTimeProvider(
            new DateTimeOffset(2026, 8, 4, 8, 0, 0, TimeSpan.Zero));
        foreach (var setting in RequiredEnvironment)
        {
            _originalEnvironment[setting.Key] = Environment.GetEnvironmentVariable(setting.Key);
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }

    public RecordingAnalyticsBackend Backend { get; }

    public FixedAnalyticsTimeProvider Clock { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAnalyticsEventStore>();
            services.RemoveAll<IPublicReadReferenceStore>();
            services.RemoveAll<IDailyListingMetricsStore>();
            services.RemoveAll<IListingMetricsAuthorizer>();
            services.RemoveAll<IAnalyticsAggregationOperationStore>();
            services.RemoveAll<IAnalyticsIdSource>();
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<IAnalyticsEventStore>(Backend);
            services.AddSingleton<IPublicReadReferenceStore>(Backend);
            services.AddSingleton<IDailyListingMetricsStore>(Backend);
            services.AddSingleton<IListingMetricsAuthorizer>(Backend);
            services.AddSingleton<IAnalyticsAggregationOperationStore>(Backend);
            services.AddSingleton<IAnalyticsIdSource>(Backend);
            services.AddSingleton<TimeProvider>(Clock);
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

    public sealed class FixedAnalyticsTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    public sealed class RecordingAnalyticsBackend :
        IAnalyticsEventStore,
        IPublicReadReferenceStore,
        IDailyListingMetricsStore,
        IListingMetricsAuthorizer,
        IAnalyticsAggregationOperationStore,
        IAnalyticsIdSource
    {
        private readonly object _gate = new();
        private readonly Dictionary<InteractionEventSemanticKey, InteractionEvent> _events = [];

        public string CatalogKey { get; set; } = "berlin-recording-services";

        public Guid PublicReadRevisionId { get; set; } =
            Guid.Parse("0198fc00-0000-7000-8000-000000000002");

        public Guid ListingId { get; set; } =
            Guid.Parse("0198fc00-0000-7000-8000-000000000003");

        public Guid AuthorizedActorId { get; set; } =
            Guid.Parse("0198fc00-0000-7000-8000-000000000004");

        public IReadOnlyList<DailyListingMetrics> Metrics { get; set; } = [];

        public AnalyticsAggregationStatusEvidence AggregationStatusEvidence { get; set; } =
            new([], LatestRun: null);

        public InteractionEvent? LastEvent { get; private set; }

        public Task<PersistedInteractionEventReceipt?> GetAsync(
            InteractionEventSemanticKey semanticKey,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(semanticKey);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _events.TryGetValue(semanticKey, out var interactionEvent);
                return Task.FromResult(
                    interactionEvent is null
                        ? null
                        : PersistedInteractionEventReceipt.FromDomain(interactionEvent));
            }
        }

        public Task<InteractionEventRegistrationResult> RegisterAsync(
            InteractionEvent interactionEvent,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(interactionEvent);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_events.TryGetValue(interactionEvent.SemanticKey, out var existing))
                {
                    var state = string.Equals(
                        existing.PayloadDigest,
                        interactionEvent.PayloadDigest,
                        StringComparison.Ordinal)
                        ? InteractionEventRegistrationState.AlreadyApplied
                        : InteractionEventRegistrationState.DigestConflict;
                    return Task.FromResult(new InteractionEventRegistrationResult(
                        state,
                        PersistedInteractionEventReceipt.FromDomain(existing)));
                }

                _events.Add(interactionEvent.SemanticKey, interactionEvent);
                LastEvent = interactionEvent;
                return Task.FromResult(new InteractionEventRegistrationResult(
                    InteractionEventRegistrationState.Stored,
                    PersistedInteractionEventReceipt.FromDomain(interactionEvent)));
            }
        }

        public Task<PublicReadMembershipResult> ValidateInteractionAsync(
            Guid publicReadRevisionId,
            string catalogKey,
            Guid? listingId,
            PlacementContext placementContext,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(placementContext);
            cancellationToken.ThrowIfCancellationRequested();
            PublicReadMembershipResult result;
            if (publicReadRevisionId != PublicReadRevisionId)
            {
                result = new PublicReadMembershipResult(
                    PublicReadMembershipState.UnknownRevision,
                    ActualCatalogKey: null,
                    ActualListingId: null);
            }
            else if (!string.Equals(catalogKey, CatalogKey, StringComparison.Ordinal))
            {
                result = new PublicReadMembershipResult(
                    PublicReadMembershipState.CatalogMismatch,
                    CatalogKey,
                    ActualListingId: null);
            }
            else if (listingId is { } requestedListingId && requestedListingId != ListingId)
            {
                result = new PublicReadMembershipResult(
                    PublicReadMembershipState.ListingNotPublic,
                    CatalogKey,
                    requestedListingId);
            }
            else if (placementContext.ExposureKind == PlacementExposureKind.Sponsored &&
                placementContext.PlacementId is null)
            {
                result = new PublicReadMembershipResult(
                    PublicReadMembershipState.SponsoredPlacementNotPublic,
                    CatalogKey,
                    listingId);
            }
            else
            {
                _ = occurredAtUtc;
                result = new PublicReadMembershipResult(
                    PublicReadMembershipState.Known,
                    CatalogKey,
                    listingId,
                    placementContext.PlacementId,
                    listingId,
                    placementContext.ScopeKey);
            }

            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<DailyListingMetrics>> GetRangeAsync(
            string catalogKey,
            Guid listingId,
            DateOnly fromInclusive,
            DateOnly toExclusive,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DailyListingMetrics> result = Metrics
                .Where(item =>
                    string.Equals(item.CatalogKey, catalogKey, StringComparison.Ordinal) &&
                    item.ListingId == listingId &&
                    item.Date >= fromInclusive &&
                    item.Date < toExclusive)
                .OrderBy(item => item.Date)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task AuthorizeAsync(
            Guid actorId,
            Guid listingId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (actorId != AuthorizedActorId || listingId != ListingId)
            {
                throw new AnalyticsCommandException(
                    "Analytics.AccessProjection",
                    "ANALYTICS_LISTING_ACCESS_DENIED",
                    403,
                    "The Analytics-local access projection does not grant this actor listing analytics access.",
                    "Verify the exact Catalog access-grant event was consumed before retrying.");
            }

            return Task.CompletedTask;
        }

        public Task<AnalyticsAggregationLease> BeginAsync(
            RebuildDailyAnalyticsMetricsRequest request,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AnalyticsAggregationLease(
                Guid.Parse("0198fc00-0000-7000-8000-000000000005"),
                request.FromInclusive,
                request.ToExclusive,
                startedAtUtc,
                startedAtUtc.AddMinutes(5),
                "test-lease-token"));
        }

        public Task CompleteAsync(
            AnalyticsAggregationLease lease,
            AnalyticsAggregateRebuildResult result,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(lease);
            ArgumentNullException.ThrowIfNull(result);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task FailAsync(
            AnalyticsAggregationLease lease,
            AnalyticsAggregationFailure failure,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(lease);
            ArgumentNullException.ThrowIfNull(failure);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<AnalyticsAggregationStatusEvidence> ReadStatusEvidenceAsync(
            DateOnly fromInclusive,
            DateOnly toExclusive,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AggregationStatusEvidence);
        }

        public Guid CreateId() => Guid.Parse("0198fc00-0000-7000-8000-000000000001");
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationSchemeName = "Analytics.Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var actorValue = Request.Headers[ActorHeader].SingleOrDefault();
            if (string.IsNullOrWhiteSpace(actorValue))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            if (!Guid.TryParse(actorValue, out var actorId))
            {
                return Task.FromResult(AuthenticateResult.Fail("Test actor header is not a UUID."));
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, actorId.ToString("D", CultureInfo.InvariantCulture)),
                new("sub", actorId.ToString("D", CultureInfo.InvariantCulture)),
            };
            var scopes = Request.Headers[ScopesHeader].SingleOrDefault();
            if (!string.IsNullOrWhiteSpace(scopes))
            {
                claims.Add(new Claim("scope", scopes));
            }

            var identity = new ClaimsIdentity(claims, AuthenticationSchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, AuthenticationSchemeName)));
        }
    }
}
