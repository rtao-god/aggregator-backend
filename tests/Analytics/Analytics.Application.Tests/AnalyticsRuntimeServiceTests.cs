using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;

namespace Analytics.Application.Tests;

public sealed class AnalyticsRuntimeServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecordHashesSessionAndPersistsExactRevisionIdentity()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var request = CreateRequest(AnalyticsInteractionKindContract.ListingView);

        var receipt = await service.RecordAsync(request, CancellationToken.None);

        Assert.False(receipt.Replayed);
        Assert.NotNull(store.Interaction);
        Assert.Equal(request.PublicReadRevisionId, store.Interaction.PublicReadRevisionId);
        Assert.NotEqual(request.SessionKey, store.Interaction.SessionHash);
        Assert.Equal(64, store.Interaction.SessionHash.Length);
        Assert.Equal(64, store.Interaction.RequestDigest.Length);
    }

    [Fact]
    public async Task ListingInteractionWithoutListingFailsBeforeStoreWrite()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var request = CreateRequest(AnalyticsInteractionKindContract.ContactClick) with
        {
            ListingId = null,
        };

        var exception = await Assert.ThrowsAsync<AnalyticsRuntimeException>(() =>
            service.RecordAsync(request, CancellationToken.None));

        Assert.Equal("ANALYTICS_LISTING_ID_REQUIRED", exception.Code);
        Assert.Null(store.Interaction);
    }

    [Fact]
    public async Task EventOutsideRetentionWindowFailsClosed()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var request = CreateRequest(AnalyticsInteractionKindContract.PageView) with
        {
            ListingId = null,
            OccurredAtUtc = Now.AddDays(-91),
        };

        var exception = await Assert.ThrowsAsync<AnalyticsRuntimeException>(() =>
            service.RecordAsync(request, CancellationToken.None));

        Assert.Equal("ANALYTICS_EVENT_TOO_OLD", exception.Code);
        Assert.Null(store.Interaction);
    }

    [Fact]
    public async Task MissingMetricsReturnExplicitZeroSnapshot()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var listingId = Guid.Parse("0198fb00-0000-7000-8000-000000000010");

        var metrics = await service.ReadListingMetricsAsync(
            "berlin-recording-services",
            listingId,
            CancellationToken.None);

        Assert.Equal(listingId, metrics.ListingId);
        Assert.Equal(0, metrics.ListingViews);
        Assert.Equal(0, metrics.ContactClicks);
        Assert.Equal(0, metrics.Leads);
    }

    private static AnalyticsRuntimeService CreateService(RecordingStore store) =>
        new(
            store,
            new AnalyticsRuntimeOptions
            {
                SessionHashKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
            },
            new FixedTimeProvider(Now));

    private static RecordAnalyticsInteractionRequest CreateRequest(
        AnalyticsInteractionKindContract kind) =>
        new(
            Guid.Parse("0198fb00-0000-7000-8000-000000000001"),
            "berlin-recording-services",
            Guid.Parse("0198fb00-0000-7000-8000-000000000002"),
            Guid.Parse("0198fb00-0000-7000-8000-000000000003"),
            "opaque-session-key",
            kind,
            Now.AddMinutes(-1));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class RecordingStore : IAnalyticsRuntimeStore
    {
        public AnalyticsInteractionRecord? Interaction { get; private set; }

        public Task<AnalyticsInteractionRegistration> RegisterAsync(
            AnalyticsInteractionRecord interaction,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interaction = interaction;
            return Task.FromResult(
                new AnalyticsInteractionRegistration(interaction.RecordedAtUtc, Replayed: false));
        }

        public Task<AnalyticsListingMetricsSnapshot?> ReadListingMetricsAsync(
            string catalogKey,
            Guid listingId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<AnalyticsListingMetricsSnapshot?>(null);
        }

        public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        public Task<AnalyticsObservationWriteResult> RecordAsync(AnalyticsObservation observation, string requestDigest, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<IReadOnlyList<AnalyticsDailyMetric>> ReadMetricsAsync(string catalogKey, Guid publicReadRevisionId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<int> AggregatePendingAsync(int maximumObservationCount, DateTimeOffset calculatedAtUtc, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
