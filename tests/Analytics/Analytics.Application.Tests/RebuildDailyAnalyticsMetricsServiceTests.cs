using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;

namespace Analytics.Application.Tests;

public sealed class RebuildDailyAnalyticsMetricsServiceTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 4, 8, 0, 0, TimeSpan.Zero);
    private static readonly RebuildDailyAnalyticsMetricsRequest Request =
        new(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 3));

    [Fact]
    public async Task RebuildUsesOnePersistedLeaseAndReturnsItsExactRunIdentity()
    {
        var runId = Guid.Parse("01990300-0000-7000-8000-000000000101");
        var leaseToken = Guid.Parse("01990300-0000-7000-8000-000000000102");
        var store = new RecordingOperationStore();
        var writer = new RecordingWriter();
        var service = new RebuildDailyAnalyticsMetricsService(
            writer,
            store,
            new SequenceIdSource(runId, leaseToken),
            new FixedTimeProvider(Timestamp));

        var result = await service.RebuildAsync(Request, CancellationToken.None);

        Assert.Equal(runId, result.RunId);
        Assert.NotNull(store.BegunLease);
        Assert.Equal(runId, store.BegunLease.RunId);
        Assert.Equal(leaseToken, store.BegunLease.LeaseToken);
        Assert.Equal(store.BegunLease, writer.Lease);
        Assert.Null(store.BlockedFailure);
    }

    [Fact]
    public async Task OwnerFailureIsRecordedAgainstTheExactLease()
    {
        var store = new RecordingOperationStore();
        var writer = new RecordingWriter
        {
            Failure = new AnalyticsCommandException(
                "Analytics.Persistence",
                "ANALYTICS_PUBLIC_READ_REFERENCE_UNAVAILABLE",
                500,
                "No public-read reference is available.",
                "Replay the exact Query activation stream."),
        };
        var service = new RebuildDailyAnalyticsMetricsService(
            writer,
            store,
            new SequenceIdSource(
                Guid.Parse("01990300-0000-7000-8000-000000000111"),
                Guid.Parse("01990300-0000-7000-8000-000000000112")),
            new FixedTimeProvider(Timestamp));

        var exception = await Assert.ThrowsAsync<AnalyticsCommandException>(() =>
            service.RebuildAsync(Request, CancellationToken.None));

        Assert.Equal("ANALYTICS_PUBLIC_READ_REFERENCE_UNAVAILABLE", exception.Code);
        Assert.Equal(store.BegunLease, store.BlockedLease);
        Assert.Equal(
            "ANALYTICS_PUBLIC_READ_REFERENCE_UNAVAILABLE",
            store.BlockedFailure?.Code);
        Assert.Equal(
            "Replay the exact Query activation stream.",
            store.BlockedFailure?.RequiredAction);
    }

    [Fact]
    public async Task OpenDateRangeIsRejectedBeforeOperationRegistration()
    {
        var store = new RecordingOperationStore();
        var writer = new RecordingWriter();
        var service = new RebuildDailyAnalyticsMetricsService(
            writer,
            store,
            new SequenceIdSource(
                Guid.Parse("01990300-0000-7000-8000-000000000121"),
                Guid.Parse("01990300-0000-7000-8000-000000000122")),
            new FixedTimeProvider(Timestamp));
        var openRange = new RebuildDailyAnalyticsMetricsRequest(
            new DateOnly(2026, 8, 3),
            new DateOnly(2026, 8, 5));

        var exception = await Assert.ThrowsAsync<AnalyticsCommandException>(() =>
            service.RebuildAsync(openRange, CancellationToken.None));

        Assert.Equal("ANALYTICS_AGGREGATION_RANGE_OPEN", exception.Code);
        Assert.Null(store.BegunLease);
        Assert.Null(writer.Lease);
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class SequenceIdSource(params Guid[] values) : IAnalyticsIdSource
    {
        private readonly Queue<Guid> _values = new(values);

        public Guid CreateId() => _values.Dequeue();
    }

    private sealed class RecordingOperationStore : IAnalyticsAggregationOperationStore
    {
        public AnalyticsAggregationLease? BegunLease { get; private set; }

        public AnalyticsAggregationLease? BlockedLease { get; private set; }

        public AnalyticsAggregationFailure? BlockedFailure { get; private set; }

        public Task<AnalyticsAggregationLease> BeginAsync(
            Guid runId,
            Guid leaseToken,
            RebuildDailyAnalyticsMetricsRequest request,
            DateTimeOffset startedAtUtc,
            DateTimeOffset leaseExpiresAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BegunLease = new AnalyticsAggregationLease(
                runId,
                leaseToken,
                request.FromInclusive,
                request.ToExclusive,
                startedAtUtc,
                leaseExpiresAtUtc);
            return Task.FromResult(BegunLease);
        }

        public Task MarkBlockedAsync(
            AnalyticsAggregationLease lease,
            AnalyticsAggregationFailure failure,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlockedLease = lease;
            BlockedFailure = failure;
            return Task.CompletedTask;
        }

        public Task<AnalyticsAggregationStatusEvidence> ReadStatusEvidenceAsync(
            DateOnly fromInclusive,
            DateOnly toExclusive,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Rebuild test does not read aggregation status.");
    }

    private sealed class RecordingWriter : IAnalyticsAggregateWriter
    {
        public AnalyticsAggregationLease? Lease { get; private set; }

        public Exception? Failure { get; init; }

        public Task<AnalyticsAggregateRebuildResult> RebuildAsync(
            AnalyticsAggregationLease lease,
            RebuildDailyAnalyticsMetricsRequest request,
            DateTimeOffset calculatedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Lease = lease;
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(new AnalyticsAggregateRebuildResult(
                lease.RunId,
                request.FromInclusive,
                request.ToExclusive,
                new string('d', 64),
                materializedDayCount: 2,
                materializedMetricCount: 3,
                removedStaleMetricCount: 0,
                calculatedAtUtc));
        }
    }
}
