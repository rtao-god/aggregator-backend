using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Analytics.Api;
using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;

namespace Analytics.Api.Tests;

public sealed class AnalyticsSummaryApiContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task SummaryReturnsObservedTotalsForCompleteRange()
    {
        using var factory = new AnalyticsApiFactory();
        var fromInclusive = new DateOnly(2026, 8, 1);
        var toExclusive = new DateOnly(2026, 8, 3);
        factory.Backend.Metrics =
        [
            Complete(factory, fromInclusive, new string('a', 64), 1),
            Complete(factory, fromInclusive.AddDays(1), new string('b', 64), 2),
        ];
        using var client = factory.CreateClient();
        using var request = CreateRequest(factory, fromInclusive, toExclusive);

        using var response = await client.SendAsync(request);
        var summary = await response.Content.ReadFromJsonAsync<ListingMetricsSummaryResponse>(
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(summary);
        Assert.Equal(AggregateReadinessStateContract.Complete, summary.Readiness);
        Assert.Equal(2, summary.SourceDayCount);
        var counts = Assert.IsType<InteractionCountsContract>(summary.Counts);
        Assert.Equal(3L, counts.OrganicImpressions);
        Assert.Matches(
            "^[0-9a-f]{64}$",
            Assert.IsType<string>(summary.AggregationSourceDigest));
        Assert.Empty(summary.UnavailableDays);
    }

    [Fact]
    public async Task SummaryDoesNotConvertPartialDayToZero()
    {
        using var factory = new AnalyticsApiFactory();
        var fromInclusive = new DateOnly(2026, 8, 1);
        var toExclusive = new DateOnly(2026, 8, 3);
        factory.Backend.Metrics =
        [
            Complete(factory, fromInclusive, new string('c', 64), 1),
            DailyListingMetrics.Unavailable(
                fromInclusive.AddDays(1),
                factory.Backend.CatalogKey,
                factory.Backend.ListingId,
                new string('d', 64),
                sourceReadRevisionCount: 1,
                AggregateReadinessState.Partial,
                "late-events"),
        ];
        using var client = factory.CreateClient();
        using var request = CreateRequest(factory, fromInclusive, toExclusive);

        using var response = await client.SendAsync(request);
        var summary = await response.Content.ReadFromJsonAsync<ListingMetricsSummaryResponse>(
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(summary);
        Assert.Equal(AggregateReadinessStateContract.Partial, summary.Readiness);
        Assert.Null(summary.Counts);
        Assert.Null(summary.AggregationSourceDigest);
        var unavailable = Assert.Single(summary.UnavailableDays);
        Assert.Equal("late-events", unavailable.Reason);
    }

    private static DailyListingMetrics Complete(
        AnalyticsApiFactory factory,
        DateOnly date,
        string sourceDigest,
        long impressions) =>
        DailyListingMetrics.Complete(
            date,
            factory.Backend.CatalogKey,
            factory.Backend.ListingId,
            sourceDigest,
            sourceReadRevisionCount: 1,
            InteractionCounts.Create(impressions, 0, 0, 0, 0, 0, 0, 0, 0));

    private static HttpRequestMessage CreateRequest(
        AnalyticsApiFactory factory,
        DateOnly fromInclusive,
        DateOnly toExclusive)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/analytics/listings/{factory.Backend.ListingId:D}/summary" +
            $"?catalogKey={factory.Backend.CatalogKey}" +
            $"&fromInclusive={fromInclusive:yyyy-MM-dd}" +
            $"&toExclusive={toExclusive:yyyy-MM-dd}");
        request.Headers.Add(AnalyticsApiFactory.AuthenticationHeader, "true");
        request.Headers.Add(
            AnalyticsApiFactory.ScopesHeader,
            AnalyticsAuthorizationPolicies.ViewListing);
        request.Headers.Add(
            AnalyticsApiFactory.ActorHeader,
            factory.Backend.AuthorizedActorId.ToString("D"));
        return request;
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
