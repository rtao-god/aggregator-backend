using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;
using Aggregator.Analytics.Infrastructure;
using Aggregator.Analytics.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Analytics.Runtime.Tests;

public sealed class AnalyticsRuntimeTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ObservationRejectsMaterialFutureTime()
    {
        var exception = Assert.Throws<AnalyticsObservationException>(() =>
            AnalyticsObservation.Create(
                Guid.CreateVersion7(),
                "berlin",
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                AnalyticsObservationKind.Impression,
                "search-card",
                "/en/providers/example",
                anonymousSessionHash: null,
                occurredAtUtc: Now + TimeSpan.FromMinutes(6),
                receivedAtUtc: Now));

        Assert.Equal("ANALYTICS_OBSERVATION_FROM_FUTURE", exception.Code);
    }

    [Fact]
    public void ObservationRejectsDirectOrMalformedSessionIdentity()
    {
        var exception = Assert.Throws<AnalyticsObservationException>(() =>
            AnalyticsObservation.Create(
                Guid.CreateVersion7(),
                "berlin",
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                AnalyticsObservationKind.DetailView,
                "detail-page",
                "/en/providers/example",
                "user@example.test",
                Now,
                Now));

        Assert.Equal("ANALYTICS_SESSION_HASH_INVALID", exception.Code);
    }

    [Fact]
    public async Task ExactObservationReplayReturnsOriginalReceipt()
    {
        var store = new InMemoryRuntimeStore();
        var service = new RecordAnalyticsObservationService(
            store,
            new FixedTimeProvider(Now));
        var request = CreateObservationRequest();

        var created = await service.RecordAsync(request, CancellationToken.None);
        var replayed = await service.RecordAsync(request, CancellationToken.None);

        Assert.False(created.Replayed);
        Assert.True(replayed.Replayed);
        Assert.Equal(created.ObservationId, replayed.ObservationId);
        Assert.Equal(created.AcceptedAtUtc, replayed.AcceptedAtUtc);
        Assert.Single(store.Observations);
    }

    [Fact]
    public async Task SameObservationIdentityWithDifferentRequestFailsClosed()
    {
        var store = new InMemoryRuntimeStore();
        var service = new RecordAnalyticsObservationService(
            store,
            new FixedTimeProvider(Now));
        var request = CreateObservationRequest();
        await service.RecordAsync(request, CancellationToken.None);
        var changed = request with { Route = "/en/providers/other" };

        var exception = await Assert.ThrowsAsync<AnalyticsRuntimeException>(() =>
            service.RecordAsync(changed, CancellationToken.None));

        Assert.Equal("ANALYTICS_OBSERVATION_ID_CONFLICT", exception.Code);
    }

    [Fact]
    public async Task MetricResponseOrderingIsDeterministic()
    {
        var revisionId = Guid.Parse("019b9b00-0000-7000-8000-000000000102");
        var firstListingId = Guid.Parse("019b9b00-0000-7000-8000-000000000001");
        var secondListingId = Guid.Parse("019b9b00-0000-7000-8000-000000000002");
        var store = new InMemoryRuntimeStore
        {
            Metrics =
            [
                CreateMetric(secondListingId, "detail-page", new DateOnly(2026, 8, 3)),
                CreateMetric(firstListingId, "search-card", new DateOnly(2026, 8, 2)),
            ],
        };
        var service = new ReadAnalyticsMetricsService(
            store,
            new FixedTimeProvider(Now));

        var response = await service.ReadAsync(
            "berlin",
            revisionId,
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 4),
            CancellationToken.None);

        Assert.Equal(
            [firstListingId, secondListingId],
            response.Items.Select(item => item.ListingId).ToArray());
        Assert.Equal(Now, response.GeneratedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10001)]
    public void WorkerRejectsUnsafeBatchSize(int batchSize)
    {
        var options = new AnalyticsAggregationWorkerOptions
        {
            BatchSize = batchSize,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void PersistenceModelOwnsAppendOnlyObservationAndRevisionedMetrics()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var observation = FindTable(model, "analytics", "interaction_observation");
        var metric = FindTable(model, "analytics", "daily_listing_metric");

        Assert.Equal(["Id"], observation.FindPrimaryKey()!.Properties.Select(item => item.Name));
        Assert.Contains(
            observation.GetCheckConstraints(),
            constraint => constraint.Name == "ck_analytics_observation_request_digest");
        Assert.DoesNotContain(
            observation.GetProperties(),
            property => property.Name is "IpAddress" or "Email" or "UserAgent" or "RawIdentity");
        var revision = metric.FindProperty("AggregateRevision");
        Assert.NotNull(revision);
        Assert.True(revision.IsConcurrencyToken);
        Assert.Contains(
            metric.GetCheckConstraints(),
            constraint => constraint.Name == "ck_analytics_daily_metric_counts");
    }

    private static RecordAnalyticsObservationRequest CreateObservationRequest() =>
        new(
            Guid.Parse("019b9b00-0000-7000-8000-000000000101"),
            "berlin",
            Guid.Parse("019b9b00-0000-7000-8000-000000000102"),
            Guid.Parse("019b9b00-0000-7000-8000-000000000103"),
            AnalyticsObservationKindContract.Impression,
            "search-card",
            "/en/providers/example",
            new string('a', 64),
            Now);

    private static AnalyticsDailyMetric CreateMetric(
        Guid listingId,
        string placementKey,
        DateOnly metricDate) =>
        new(
            "berlin",
            Guid.Parse("019b9b00-0000-7000-8000-000000000102"),
            listingId,
            placementKey,
            metricDate,
            ImpressionCount: 10,
            DetailViewCount: 4,
            ExternalClickCount: 2,
            LeadCount: 1,
            ConversionCount: 0,
            CalculatedAtUtc: Now,
            AggregateRevision: 1);

    private static AnalyticsRuntimeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AnalyticsRuntimeDbContext>()
            .UseNpgsql("Host=localhost;Database=analytics_db;Username=analytics_app;Password=test")
            .Options;
        return new AnalyticsRuntimeDbContext(options);
    }

    private static IEntityType FindTable(IModel model, string schema, string tableName) =>
        model.GetEntityTypes().Single(entity =>
            string.Equals(entity.GetSchema(), schema, StringComparison.Ordinal) &&
            string.Equals(entity.GetTableName(), tableName, StringComparison.Ordinal));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class InMemoryRuntimeStore : IAnalyticsRuntimeStore
    {
        private readonly Dictionary<Guid, (string Digest, AnalyticsObservation Observation)> _observations = [];

        public IReadOnlyCollection<AnalyticsObservation> Observations =>
            _observations.Values.Select(value => value.Observation).ToArray();

        public IReadOnlyList<AnalyticsDailyMetric> Metrics { get; init; } = [];

        public Task<AnalyticsObservationWriteResult> RecordAsync(
            AnalyticsObservation observation,
            string requestDigest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_observations.TryGetValue(observation.Id, out var existing))
            {
                if (!string.Equals(existing.Digest, requestDigest, StringComparison.Ordinal))
                {
                    throw new AnalyticsRuntimeException(
                        "Analytics.Interactions",
                        "ANALYTICS_OBSERVATION_ID_CONFLICT",
                        409,
                        "The observation ID was already registered with a different request digest.",
                        "Replay the exact original request or submit a new observation ID.");
                }

                return Task.FromResult(new AnalyticsObservationWriteResult(
                    observation.Id,
                    existing.Digest,
                    existing.Observation.ReceivedAtUtc,
                    Replayed: true));
            }

            _observations.Add(observation.Id, (requestDigest, observation));
            return Task.FromResult(new AnalyticsObservationWriteResult(
                observation.Id,
                requestDigest,
                observation.ReceivedAtUtc,
                Replayed: false));
        }

        public Task<IReadOnlyList<AnalyticsDailyMetric>> ReadMetricsAsync(
            string catalogKey,
            Guid publicReadRevisionId,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AnalyticsDailyMetric> result = Metrics
                .Where(metric =>
                    metric.CatalogKey == catalogKey &&
                    metric.PublicReadRevisionId == publicReadRevisionId &&
                    metric.MetricDate >= fromDate &&
                    metric.MetricDate <= toDate)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<int> AggregatePendingAsync(
            int maximumObservationCount,
            DateTimeOffset calculatedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Math.Min(maximumObservationCount, _observations.Count));
        }

        public Task<AnalyticsInteractionRegistration> RegisterAsync(AnalyticsInteractionRecord interaction, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<AnalyticsListingMetricsSnapshot?> ReadListingMetricsAsync(string catalogKey, Guid listingId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
