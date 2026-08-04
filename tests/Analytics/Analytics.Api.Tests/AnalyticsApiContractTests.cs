using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Analytics.Api;
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
