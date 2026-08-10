using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

/// <summary>Identifies one lease-bound aggregation execution accepted by the Analytics owner.</summary>
public sealed record AnalyticsAggregationLease(
    Guid RunId,
    Guid LeaseToken,
    DateOnly FromInclusive,
    DateOnly ToExclusive,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LeaseExpiresAtUtc);

/// <summary>Preserves the owner failure that blocked one aggregation run.</summary>
public sealed record AnalyticsAggregationFailure(
    string Code,
    string Detail,
    string RequiredAction,
    DateTimeOffset FailedAtUtc);

/// <summary>Provides the exact persisted evidence required to interpret one aggregation range.</summary>
public sealed record AnalyticsAggregationStatusEvidence(
    IReadOnlyList<AnalyticsAggregateDayReadiness> CompletedDays,
    AnalyticsAggregateRun? LatestRun);

/// <summary>Owns durable aggregation-run registration, failure, and status evidence.</summary>
public interface IAnalyticsAggregationOperationStore
{
    public Task<AnalyticsAggregationLease> BeginAsync(
        Guid runId,
        Guid leaseToken,
        RebuildDailyAnalyticsMetricsRequest request,
        DateTimeOffset startedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);

    public Task MarkBlockedAsync(
        AnalyticsAggregationLease lease,
        AnalyticsAggregationFailure failure,
        CancellationToken cancellationToken);

    public Task<AnalyticsAggregationStatusEvidence> ReadStatusEvidenceAsync(
        DateOnly fromInclusive,
        DateOnly toExclusive,
        CancellationToken cancellationToken);
}
