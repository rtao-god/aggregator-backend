using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Analytics.Api;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;

namespace Analytics.Api.Tests;

public sealed class AnalyticsApiContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task LivenessIsAnonymousAndReadOnly()
    {
        using var factory = new AnalyticsApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Analytics.Runtime", document.RootElement.GetProperty("owner").GetString());
        Assert.Equal("live", document.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task PublicInteractionUsesExactAntiAbuseProofAndSemanticReplay()
    {
        using var factory = new AnalyticsApiFactory();
        using var client = factory.CreateClient();
        var occurredAtUtc = factory.Clock.GetUtcNow().AddSeconds(-1);
        var clientEventId = Guid.Parse("0198fc00-0000-7000-8000-000000000101");
        var token = await IssueTokenAsync(client, clientEventId, occurredAtUtc);
        var request = CreateRequest(factory, clientEventId, occurredAtUtc, token.Token);

        using var firstResponse = await client.PostAsJsonAsync(
            "/api/analytics/interaction-events",
            request,
            JsonOptions);
        using var replayResponse = await client.PostAsJsonAsync(
            "/api/analytics/interaction-events",
            request,
            JsonOptions);
        var first = await firstResponse.Content.ReadFromJsonAsync<InteractionEventResponse>(JsonOptions);
        var replay = await replayResponse.Content.ReadFromJsonAsync<InteractionEventResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.NotNull(first);
        Assert.NotNull(replay);
        Assert.Equal(InteractionAcceptanceStateContract.Accepted, first.AcceptanceState);
        Assert.Equal(InteractionAcceptanceStateContract.AlreadyApplied, replay.AcceptanceState);
        Assert.Equal(first.EventId, replay.EventId);
        Assert.NotNull(factory.Backend.LastEvent);
        Assert.Equal(factory.Backend.PublicReadRevisionId, factory.Backend.LastEvent.PublicReadRevisionId);
    }

    [Fact]
    public async Task InteractionBatchReturnsExactAcceptedAndReplayCounts()
    {
        using var factory = new AnalyticsApiFactory();
        using var client = factory.CreateClient();
        var occurredAtUtc = factory.Clock.GetUtcNow().AddSeconds(-1);
        var firstId = Guid.Parse("0198fc00-0000-7000-8000-000000000201");
        var secondId = Guid.Parse("0198fc00-0000-7000-8000-000000000202");
        var firstToken = await IssueTokenAsync(client, firstId, occurredAtUtc);
        var secondToken = await IssueTokenAsync(client, secondId, occurredAtUtc);
        var batch = new SubmitInteractionEventBatchRequest(
        [
            CreateRequest(factory, firstId, occurredAtUtc, firstToken.Token),
            CreateRequest(factory, secondId, occurredAtUtc, secondToken.Token),
        ]);

        using var firstResponse = await client.PostAsJsonAsync(
            "/api/analytics/interaction-events/batch",
            batch,
            JsonOptions);
        using var replayResponse = await client.PostAsJsonAsync(
            "/api/analytics/interaction-events/batch",
            batch,
            JsonOptions);
        var first = await firstResponse.Content.ReadFromJsonAsync<InteractionEventBatchResponse>(JsonOptions);
        var replay = await replayResponse.Content.ReadFromJsonAsync<InteractionEventBatchResponse>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.NotNull(first);
        Assert.NotNull(replay);
        Assert.Equal(2, first.AcceptedCount);
        Assert.Equal(0, first.AlreadyAppliedCount);
        Assert.Equal(0, first.RejectedCount);
        Assert.All(first.Items, item =>
            Assert.Equal(InteractionEventBatchItemStateContract.Accepted, item.State));
        Assert.Equal(0, replay.AcceptedCount);
        Assert.Equal(2, replay.AlreadyAppliedCount);
        Assert.Equal(0, replay.RejectedCount);
        Assert.All(replay.Items, item =>
            Assert.Equal(InteractionEventBatchItemStateContract.AlreadyApplied, item.State));
    }

    [Fact]
    public async Task AntiAbuseProofCannotBeUsedForAnotherEventIdentity()
    {
        using var factory = new AnalyticsApiFactory();
        using var client = factory.CreateClient();
        var occurredAtUtc = factory.Clock.GetUtcNow().AddSeconds(-1);
        var originalEventId = Guid.Parse("0198fc00-0000-7000-8000-000000000111");
        var token = await IssueTokenAsync(client, originalEventId, occurredAtUtc);
        var changedEventId = Guid.Parse("0198fc00-0000-7000-8000-000000000112");
        var request = CreateRequest(factory, changedEventId, occurredAtUtc, token.Token);

        using var response = await client.PostAsJsonAsync(
            "/api/analytics/interaction-events",
            request,
            JsonOptions);
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "ANALYTICS_ANTI_ABUSE_TOKEN_INVALID",
            document.RootElement.GetProperty("code").GetString());
        Assert.Null(factory.Backend.LastEvent);
    }

    [Fact]
    public async Task UnknownJsonMemberIsRejectedBeforeOwnerWrite()
    {
        using var factory = new AnalyticsApiFactory();
        using var client = factory.CreateClient();
        var occurredAtUtc = factory.Clock.GetUtcNow().AddSeconds(-1);
        var clientEventId = Guid.Parse("0198fc00-0000-7000-8000-000000000121");
        var token = await IssueTokenAsync(client, clientEventId, occurredAtUtc);
        var json = $$"""
            {
              "clientEventId":"{{clientEventId}}",
              "eventKind":"listingImpression",
              "catalogKey":"{{factory.Backend.CatalogKey}}",
              "listingId":"{{factory.Backend.ListingId}}",
              "publicReadRevisionId":"{{factory.Backend.PublicReadRevisionId}}",
              "occurredAtUtc":"{{occurredAtUtc:O}}",
              "pageContext":"search-results",
              "placementContext":{"exposureKind":"organic","placementId":null,"scopeKey":"recording-studio"},
              "referrerClass":"internal",
              "campaignParameters":{},
              "consentMode":"analyticsAllowed",
              "antiAbuseToken":"{{token.Token}}",
              "unexpected":true
            }
            """;
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(
            "/api/analytics/interaction-events",
            content);
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "ANALYTICS_REQUEST_CONTRACT_INVALID",
            document.RootElement.GetProperty("code").GetString());
        Assert.Null(factory.Backend.LastEvent);
    }

    [Fact]
    public async Task NumericEnumTokenIsRejectedByWireContract()
    {
        using var factory = new AnalyticsApiFactory();
        using var client = factory.CreateClient();
        var occurredAtUtc = factory.Clock.GetUtcNow().AddSeconds(-1);
        var clientEventId = Guid.Parse("0198fc00-0000-7000-8000-000000000131");
        var token = await IssueTokenAsync(client, clientEventId, occurredAtUtc);
        var json = $$"""
            {
              "clientEventId":"{{clientEventId}}",
              "eventKind":2,
              "catalogKey":"{{factory.Backend.CatalogKey}}",
              "listingId":"{{factory.Backend.ListingId}}",
              "publicReadRevisionId":"{{factory.Backend.PublicReadRevisionId}}",
              "occurredAtUtc":"{{occurredAtUtc:O}}",
              "pageContext":"search-results",
              "placementContext":{"exposureKind":"organic","placementId":null,"scopeKey":"recording-studio"},
              "referrerClass":"internal",
              "campaignParameters":{},
              "consentMode":"analyticsAllowed",
              "antiAbuseToken":"{{token.Token}}"
            }
            """;
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(
            "/api/analytics/interaction-events",
            content);
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "ANALYTICS_REQUEST_CONTRACT_INVALID",
            document.RootElement.GetProperty("code").GetString());
        Assert.Null(factory.Backend.LastEvent);
    }

    [Fact]
    public async Task MetricsReadRequiresAuthenticationAndListingActorMapping()
    {
        using var factory = new AnalyticsApiFactory();
        using var client = factory.CreateClient();
        var path = MetricsPath(factory, new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 4));

        using var anonymousResponse = await client.GetAsync(path);
        using var anonymousDocument = await ReadJsonAsync(anonymousResponse);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(
            "AUTHENTICATION_REQUIRED",
            anonymousDocument.RootElement.GetProperty("code").GetString());

        using var missingActorRequest = new HttpRequestMessage(HttpMethod.Get, path);
        Authenticate(
            missingActorRequest,
            AnalyticsAuthorizationPolicies.ViewListing,
            actorId: null);
        using var missingActorResponse = await client.SendAsync(missingActorRequest);
        using var missingActorDocument = await ReadJsonAsync(missingActorResponse);
        Assert.Equal(HttpStatusCode.Forbidden, missingActorResponse.StatusCode);
        Assert.Equal(
            "ANALYTICS_ACTOR_MAPPING_REQUIRED",
            missingActorDocument.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CompleteAggregateCanExposeObservedZeroCounts()
    {
        using var factory = new AnalyticsApiFactory();
        var date = new DateOnly(2026, 8, 3);
        factory.Backend.Metrics =
        [
            DailyListingMetrics.Complete(
                date,
                factory.Backend.CatalogKey,
                factory.Backend.ListingId,
                new string('a', 64),
                sourceReadRevisionCount: 1,
                InteractionCounts.Create(0, 0, 0, 0, 0, 0, 0, 0, 0)),
        ];
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            MetricsPath(factory, date, date.AddDays(1)));
        Authenticate(
            request,
            AnalyticsAuthorizationPolicies.ViewListing,
            factory.Backend.AuthorizedActorId);

        using var response = await client.SendAsync(request);
        var metrics = await response.Content.ReadFromJsonAsync<DailyListingMetricsResponse[]>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(metrics ?? []);
        Assert.Equal(AggregateReadinessStateContract.Complete, item.Readiness);
        Assert.NotNull(item.Counts);
        Assert.Equal(0, item.Counts.OrganicImpressions);
    }

    [Fact]
    public async Task MissingAggregateDateIsUnavailableNotZero()
    {
        using var factory = new AnalyticsApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            MetricsPath(factory, new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 4)));
        Authenticate(
            request,
            AnalyticsAuthorizationPolicies.ViewListing,
            factory.Backend.AuthorizedActorId);

        using var response = await client.SendAsync(request);
        using var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "ANALYTICS_AGGREGATE_COVERAGE_INCOMPLETE",
            document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AggregationStatusRequiresDedicatedScope()
    {
        using var factory = new AnalyticsApiFactory();
        using var client = factory.CreateClient();
        var path = AggregationStatusPath(
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3));

        using var anonymousResponse = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var wrongScopeRequest = new HttpRequestMessage(HttpMethod.Get, path);
        Authenticate(
            wrongScopeRequest,
            AnalyticsAuthorizationPolicies.ViewListing,
            actorId: null);
        using var wrongScopeResponse = await client.SendAsync(wrongScopeRequest);
        Assert.Equal(HttpStatusCode.Forbidden, wrongScopeResponse.StatusCode);
    }

    [Fact]
    public async Task AggregationStatusReturnsExactCompleteDayEvidence()
    {
        using var factory = new AnalyticsApiFactory();
        var fromInclusive = new DateOnly(2026, 8, 1);
        var toExclusive = new DateOnly(2026, 8, 3);
        factory.Backend.AggregationStatusEvidence = new AnalyticsAggregationStatusEvidence(
        [
            AnalyticsAggregateDayReadiness.Create(
                fromInclusive,
                Guid.Parse("0198fc00-0000-7000-8000-000000000301"),
                new string('a', 64),
                metricCount: 2,
                factory.Clock.GetUtcNow().AddMinutes(-2)),
            AnalyticsAggregateDayReadiness.Create(
                fromInclusive.AddDays(1),
                Guid.Parse("0198fc00-0000-7000-8000-000000000302"),
                new string('b', 64),
                metricCount: 3,
                factory.Clock.GetUtcNow().AddMinutes(-1)),
        ],
        LatestRun: null);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            AggregationStatusPath(fromInclusive, toExclusive));
        Authenticate(
            request,
            AnalyticsAuthorizationPolicies.ViewAggregationStatus,
            actorId: null);

        using var response = await client.SendAsync(request);
        var status = await response.Content.ReadFromJsonAsync<AnalyticsAggregationStatusResponse>(
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(status);
        Assert.Equal(AggregateReadinessStateContract.Complete, status.Readiness);
        Assert.Empty(status.MissingDates);
        Assert.Null(status.LatestRun);
        Assert.Null(status.UnavailableReason);
    }

    [Fact]
    public async Task AggregationStatusPreservesBlockedOwnerFailure()
    {
        using var factory = new AnalyticsApiFactory();
        var fromInclusive = new DateOnly(2026, 8, 1);
        var toExclusive = new DateOnly(2026, 8, 3);
        var blockedRun = AnalyticsAggregateRun.Restore(
            Guid.Parse("0198fc00-0000-7000-8000-000000000310"),
            fromInclusive,
            toExclusive,
            AnalyticsAggregateRunState.Blocked,
            factory.Clock.GetUtcNow().AddMinutes(-2),
            factory.Clock.GetUtcNow().AddMinutes(-1),
            sourceDigest: null,
            materializedMetricCount: null,
            removedStaleMetricCount: null,
            materializedDayCount: null,
            failureCode: "ANALYTICS_SOURCE_PROJECTION_BLOCKED",
            failureDetail: "Exact public-read projection is not available.",
            requiredAction: "Replay the exact Query activation stream.");
        factory.Backend.AggregationStatusEvidence = new AnalyticsAggregationStatusEvidence(
            CompletedDays: [],
            LatestRun: blockedRun);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            AggregationStatusPath(fromInclusive, toExclusive));
        Authenticate(
            request,
            AnalyticsAuthorizationPolicies.ViewAggregationStatus,
            actorId: null);

        using var response = await client.SendAsync(request);
        var status = await response.Content.ReadFromJsonAsync<AnalyticsAggregationStatusResponse>(
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(status);
        Assert.Equal(AggregateReadinessStateContract.Blocked, status.Readiness);
        Assert.Equal(
            "ANALYTICS_SOURCE_PROJECTION_BLOCKED",
            status.UnavailableReason);
        Assert.Equal(
            "Replay the exact Query activation stream.",
            status.LatestRun?.RequiredAction);
    }

    private static SubmitInteractionEventRequest CreateRequest(
        AnalyticsApiFactory factory,
        Guid clientEventId,
        DateTimeOffset occurredAtUtc,
        string antiAbuseToken) =>
        new(
            clientEventId,
            InteractionEventKindContract.ListingImpression,
            factory.Backend.CatalogKey,
            factory.Backend.ListingId,
            factory.Backend.PublicReadRevisionId,
            occurredAtUtc,
            "search-results",
            new PlacementContextContract(
                PlacementExposureKindContract.Organic,
                PlacementId: null,
                ScopeKey: "recording-studio"),
            ReferrerClassContract.Internal,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ConsentModeContract.AnalyticsAllowed,
            antiAbuseToken);

    private static async Task<AnalyticsAntiAbuseTokenResponse> IssueTokenAsync(
        HttpClient client,
        Guid clientEventId,
        DateTimeOffset occurredAtUtc)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/analytics/anti-abuse-tokens",
            new IssueAnalyticsAntiAbuseTokenRequest(clientEventId, occurredAtUtc),
            JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AnalyticsAntiAbuseTokenResponse>(JsonOptions)
            ?? throw new InvalidOperationException("Analytics anti-abuse token response is missing.");
    }

    private static string AggregationStatusPath(
        DateOnly fromInclusive,
        DateOnly toExclusive) =>
        "/api/analytics/aggregation-status" +
        $"?fromInclusive={fromInclusive:yyyy-MM-dd}" +
        $"&toExclusive={toExclusive:yyyy-MM-dd}";

    private static string MetricsPath(
        AnalyticsApiFactory factory,
        DateOnly fromInclusive,
        DateOnly toExclusive) =>
        $"/api/analytics/listings/{factory.Backend.ListingId:D}/daily-metrics" +
        $"?catalogKey={factory.Backend.CatalogKey}" +
        $"&fromInclusive={fromInclusive:yyyy-MM-dd}" +
        $"&toExclusive={toExclusive:yyyy-MM-dd}";

    private static void Authenticate(
        HttpRequestMessage request,
        string scope,
        Guid? actorId)
    {
        request.Headers.Add(AnalyticsApiFactory.AuthenticationHeader, "true");
        request.Headers.Add(AnalyticsApiFactory.ScopesHeader, scope);
        if (actorId is { } value)
        {
            request.Headers.Add(AnalyticsApiFactory.ActorHeader, value.ToString("D"));
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}
