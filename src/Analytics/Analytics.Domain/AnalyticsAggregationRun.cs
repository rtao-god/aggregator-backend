namespace Aggregator.Analytics.Domain;

/// <summary>Declares the persisted lifecycle of one exact daily aggregation operation.</summary>
public enum AnalyticsAggregateRunState
{
    Rebuilding = 1,
    Complete = 2,
    Blocked = 3,
}

/// <summary>Represents one immutable completed date unit selected by the current aggregate-readiness projection.</summary>
public sealed record AnalyticsAggregateDayReadiness
{
    private AnalyticsAggregateDayReadiness(
        DateOnly date,
        Guid runId,
        string sourceDigest,
        int metricCount,
        DateTimeOffset completedAtUtc)
    {
        Date = date;
        RunId = runId;
        SourceDigest = sourceDigest;
        MetricCount = metricCount;
        CompletedAtUtc = completedAtUtc;
    }

    public DateOnly Date { get; }

    public Guid RunId { get; }

    public string SourceDigest { get; }

    public int MetricCount { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public static AnalyticsAggregateDayReadiness Create(
        DateOnly date,
        Guid runId,
        string sourceDigest,
        int metricCount,
        DateTimeOffset completedAtUtc)
    {
        AnalyticsDomainRules.RequireIdentifier(runId, nameof(runId));
        var normalizedDigest = AnalyticsDomainRules.RequireDigest(sourceDigest, nameof(sourceDigest));
        AnalyticsDomainRules.RequireUtc(completedAtUtc, nameof(completedAtUtc));
        if (metricCount < 0)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_AGGREGATE_DAY_METRIC_COUNT_INVALID",
                "Aggregate day metric count cannot be negative.");
        }

        return new AnalyticsAggregateDayReadiness(
            date,
            runId,
            normalizedDigest,
            metricCount,
            completedAtUtc);
    }
}

/// <summary>Represents persisted evidence for one exact aggregation operation.</summary>
public sealed record AnalyticsAggregateRun
{
    private AnalyticsAggregateRun(
        Guid runId,
        DateOnly fromInclusive,
        DateOnly toExclusive,
        AnalyticsAggregateRunState state,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? sourceDigest,
        int? materializedMetricCount,
        int? removedStaleMetricCount,
        int? materializedDayCount,
        string? failureCode,
        string? failureDetail,
        string? requiredAction)
    {
        RunId = runId;
        FromInclusive = fromInclusive;
        ToExclusive = toExclusive;
        State = state;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        SourceDigest = sourceDigest;
        MaterializedMetricCount = materializedMetricCount;
        RemovedStaleMetricCount = removedStaleMetricCount;
        MaterializedDayCount = materializedDayCount;
        FailureCode = failureCode;
        FailureDetail = failureDetail;
        RequiredAction = requiredAction;
    }

    public Guid RunId { get; }

    public DateOnly FromInclusive { get; }

    public DateOnly ToExclusive { get; }

    public AnalyticsAggregateRunState State { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; }

    public string? SourceDigest { get; }

    public int? MaterializedMetricCount { get; }

    public int? RemovedStaleMetricCount { get; }

    public int? MaterializedDayCount { get; }

    public string? FailureCode { get; }

    public string? FailureDetail { get; }

    public string? RequiredAction { get; }

    public static AnalyticsAggregateRun Restore(
        Guid runId,
        DateOnly fromInclusive,
        DateOnly toExclusive,
        AnalyticsAggregateRunState state,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? sourceDigest,
        int? materializedMetricCount,
        int? removedStaleMetricCount,
        int? materializedDayCount,
        string? failureCode,
        string? failureDetail,
        string? requiredAction)
    {
        AnalyticsDomainRules.RequireIdentifier(runId, nameof(runId));
        AnalyticsDomainRules.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        var dayCount = toExclusive.DayNumber - fromInclusive.DayNumber;
        if (dayCount is < 1 or > 31)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_AGGREGATE_RUN_RANGE_INVALID",
                "Aggregate run range must contain between 1 and 31 UTC days using [from, to) semantics.");
        }

        if (!Enum.IsDefined(state))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_AGGREGATE_RUN_STATE_INVALID",
                $"Aggregate run state '{state}' is unsupported.");
        }

        if (completedAtUtc is { } completed)
        {
            AnalyticsDomainRules.RequireUtc(completed, nameof(completedAtUtc));
            if (completed < startedAtUtc)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_AGGREGATE_RUN_TIME_INVALID",
                    "Aggregate run completion cannot precede its start.");
            }
        }

        ValidateStateShape(
            state,
            dayCount,
            completedAtUtc,
            sourceDigest,
            materializedMetricCount,
            removedStaleMetricCount,
            materializedDayCount,
            failureCode,
            failureDetail,
            requiredAction);
        return new AnalyticsAggregateRun(
            runId,
            fromInclusive,
            toExclusive,
            state,
            startedAtUtc,
            completedAtUtc,
            sourceDigest,
            materializedMetricCount,
            removedStaleMetricCount,
            materializedDayCount,
            failureCode,
            failureDetail,
            requiredAction);
    }

    private static void ValidateStateShape(
        AnalyticsAggregateRunState state,
        int expectedDayCount,
        DateTimeOffset? completedAtUtc,
        string? sourceDigest,
        int? materializedMetricCount,
        int? removedStaleMetricCount,
        int? materializedDayCount,
        string? failureCode,
        string? failureDetail,
        string? requiredAction)
    {
        if (state == AnalyticsAggregateRunState.Rebuilding)
        {
            if (completedAtUtc is not null || sourceDigest is not null ||
                materializedMetricCount is not null || removedStaleMetricCount is not null ||
                materializedDayCount is not null || failureCode is not null ||
                failureDetail is not null || requiredAction is not null)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_AGGREGATE_RUN_REBUILDING_SHAPE_INVALID",
                    "Rebuilding aggregate run cannot contain terminal result or failure fields.");
            }

            return;
        }

        if (completedAtUtc is null)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_AGGREGATE_RUN_COMPLETION_REQUIRED",
                "Terminal aggregate run requires a completion timestamp.");
        }

        if (state == AnalyticsAggregateRunState.Complete)
        {
            _ = AnalyticsDomainRules.RequireDigest(
                sourceDigest ?? string.Empty,
                nameof(sourceDigest));
            if (materializedMetricCount is null or < 0 ||
                removedStaleMetricCount is null or < 0 ||
                materializedDayCount != expectedDayCount || failureCode is not null ||
                failureDetail is not null || requiredAction is not null)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_AGGREGATE_RUN_COMPLETE_SHAPE_INVALID",
                    "Complete aggregate run requires exact day coverage, non-negative result counts, and no failure fields.");
            }

            return;
        }

        if (sourceDigest is not null || materializedMetricCount is not null ||
            removedStaleMetricCount is not null || materializedDayCount is not null ||
            string.IsNullOrWhiteSpace(failureCode) || string.IsNullOrWhiteSpace(failureDetail) ||
            string.IsNullOrWhiteSpace(requiredAction))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_AGGREGATE_RUN_BLOCKED_SHAPE_INVALID",
                "Blocked aggregate run requires exact failure fields and no success result.");
        }
    }
}
