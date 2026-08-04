using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;

namespace Analytics.Api.Tests;

public sealed class AnalyticsApiContractTests
{
    [Fact]
    public async Task PublicInteractionIntakeReturnsReceiptAndHashesSession()
    {
        using var factory = new AnalyticsApiFactory();
        using var client = factory.CreateClient();
        var request = CreateRequest();

        using var response = await client.PostAsJsonAsync(
            "/api/analytics/interactions",
            request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<AnalyticsInteractionReceipt>();
        Assert.NotNull(receipt);
        Assert.Equal(request.EventId, receipt.EventId);
        Assert.NotNull(factory.Store.Interaction);
        Assert.NotEqual(request.SessionKey, factory.Store.Interaction.SessionHash);
    }

    [Fact]
    public async Task UnknownJsonMemberIsRejectedBeforeStoreWrite()
    {
        using var factory = new AnalyticsApiFactory();
        using var client = factory.CreateClient();
        var json = $$"""
            {
              "eventId":"{{Guid.Parse("0198fc00-0000-7000-8000-000000000001")}}",
              "catalogKey":"berlin-recording-services",
              "publicReadRevisionId":"{{Guid.Parse("0198fc00-0000-7000-8000-000000000002")}}",
              "listingId":"{{Guid.Parse("0198fc00-0000-7000-8000-000000000003")}}",
              "sessionKey":"session",
              "kind":"listingView",
              "occurredAtUtc":"2026-08-04T06:00:00+00:00",
              "unexpected":true
            }
            """;
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/analytics/interactions", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(factory.Store.Interaction);
    }

    [Fact]
    public async Task InternalMetricsReadRejectsMissingKey()
    {
        using var factory = new AnalyticsApiFactory();
        using var client = factory.CreateClient();
        var listingId = Guid.Parse("0198fc00-0000-7000-8000-000000000010");

        using var response = await client.GetAsync(
            $"/api/analytics/catalogs/berlin-recording-services/listings/{listingId:D}/metrics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InternalMetricsReadReturnsOwnedAggregate()
    {
        using var factory = new AnalyticsApiFactory();
        var listingId = Guid.Parse("0198fc00-0000-7000-8000-000000000020");
        factory.Store.Metrics = new AnalyticsListingMetricsSnapshot(
            "berlin-recording-services",
            listingId,
            12,
            4,
            2,
            new DateTimeOffset(2026, 8, 4, 6, 0, 0, TimeSpan.Zero));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "X-Analytics-Internal-Key",
            AnalyticsApiFactory.InternalMetricsKey);

        using var response = await client.GetAsync(
            $"/api/analytics/catalogs/berlin-recording-services/listings/{listingId:D}/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var metrics = await response.Content.ReadFromJsonAsync<AnalyticsListingMetricsResponse>();
        Assert.NotNull(metrics);
        Assert.Equal(12, metrics.ListingViews);
        Assert.Equal(4, metrics.ContactClicks);
        Assert.Equal(2, metrics.Leads);
    }

    [Fact]
    public async Task ReadinessFailsClosedWhenOwnerSchemaIsUnavailable()
    {
        using var factory = new AnalyticsApiFactory();
        factory.Store.Ready = false;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unavailable", document.RootElement.GetProperty("state").GetString());
    }

    private static RecordAnalyticsInteractionRequest CreateRequest() =>
        new(
            Guid.Parse("0198fc00-0000-7000-8000-000000000001"),
            "berlin-recording-services",
            Guid.Parse("0198fc00-0000-7000-8000-000000000002"),
            Guid.Parse("0198fc00-0000-7000-8000-000000000003"),
            "opaque-session-key",
            AnalyticsInteractionKindContract.ListingView,
            DateTimeOffset.UtcNow.AddSeconds(-1));
}
