namespace Aggregator.Analytics.Application;

/// <summary>Requests deterministic materialization for a closed UTC date range.</summary>
public sealed record RebuildDailyAnalyticsMetricsRequest(
    DateOnly FromInclusive,
    DateOnly ToExclusive);

/// <summary>Reports the exact bounded effect of one Analytics aggregate rebuild.</summary>
public sealed record AnalyticsAggregateRebuildResult(
    Guid RunId,
    DateOnly FromInclusive,
    DateOnly ToExclusive,
    string SourceDigest,
    int MaterializedDayCount,
    int MaterializedMetricCount,
    int RemovedStaleMetricCount,
    DateTimeOffset CompletedAtUtc);

/// <summary>Persists complete daily aggregates and completes the exact lease-bound run atomically.</summary>
public interface IAnalyticsAggregateWriter
{
    public Task<AnalyticsAggregateRebuildResult> RebuildAsync(
        AnalyticsAggregationLease lease,
        RebuildDailyAnalyticsMetricsRequest request,
        DateTimeOffset calculatedAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Owns validation and durable orchestration of closed-range daily Analytics materialization.</summary>
public sealed class RebuildDailyAnalyticsMetricsService(
    IAnalyticsAggregateWriter aggregateWriter,
    IAnalyticsAggregationOperationStore operationStore,
    IAnalyticsIdSource idSource,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);

    public async Task<AnalyticsAggregateRebuildResult> RebuildAsync(
        RebuildDailyAnalyticsMetricsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAtUtc = timeProvider.GetUtcNow();
        var todayUtc = DateOnly.FromDateTime(startedAtUtc.UtcDateTime);
        if (request.ToExclusive <= request.FromInclusive)
        {
            throw InvalidRange(
                "ANALYTICS_AGGREGATION_RANGE_EMPTY",
                "Analytics aggregate range must be non-empty and use [from, to) semantics.");
        }

        if (request.ToExclusive > todayUtc)
        {
            throw InvalidRange(
                "ANALYTICS_AGGREGATION_RANGE_OPEN",
                "Analytics aggregate range cannot include the current or a future UTC day.");
        }

        if (request.ToExclusive.DayNumber - request.FromInclusive.DayNumber > 31)
        {
            throw InvalidRange(
                "ANALYTICS_AGGREGATION_RANGE_TOO_LARGE",
                "One Analytics aggregate rebuild cannot exceed 31 UTC days.");
        }

        var lease = await operationStore.BeginAsync(
            idSource.CreateId(),
            idSource.CreateId(),
            request,
            startedAtUtc,
            startedAtUtc.Add(LeaseDuration),
            cancellationToken);
        try
        {
            return await aggregateWriter.RebuildAsync(
                lease,
                request,
                timeProvider.GetUtcNow(),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = ToFailure(exception, timeProvider.GetUtcNow());
            try
            {
                await operationStore.MarkBlockedAsync(
                    lease,
                    failure,
                    CancellationToken.None);
            }
            catch (Exception recordingFailure)
            {
                throw new AnalyticsCommandException(
                    "Analytics.Persistence",
                    "ANALYTICS_AGGREGATION_FAILURE_NOT_RECORDED",
                    500,
                    "Analytics aggregate failed and its durable failure record could not be committed.",
                    "Stop aggregation and repair the aggregation operation ledger before retrying.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["runId"] = lease.RunId,
                        ["aggregationFailureType"] = exception.GetType().FullName,
                        ["recordingFailureType"] = recordingFailure.GetType().FullName,
                    });
            }

            throw;
        }
    }

    private static AnalyticsAggregationFailure ToFailure(
        Exception exception,
        DateTimeOffset failedAtUtc) => exception switch
    {
        AnalyticsCommandException ownerFailure => new AnalyticsAggregationFailure(
            ownerFailure.Code,
            ownerFailure.Message,
            ownerFailure.RequiredAction,
            failedAtUtc),
        _ => new AnalyticsAggregationFailure(
            "ANALYTICS_AGGREGATION_UNEXPECTED_FAILURE",
            "Analytics aggregate materialization failed with an unexpected owner error.",
            "Inspect the correlated worker failure and repair the causal Analytics owner before retrying.",
            failedAtUtc),
    };

    private static AnalyticsCommandException InvalidRange(string code, string message) =>
        new(
            "Analytics.Aggregation",
            code,
            400,
            message,
            "Submit a bounded closed UTC date range and preserve [from, to) semantics.");
}
