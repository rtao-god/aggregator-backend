using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

/// <summary>Interprets persisted run and day evidence for one exact aggregate date range.</summary>
public sealed class ReadAnalyticsAggregationStatusService(
    IAnalyticsAggregationOperationStore operationStore)
{
    public async Task<AnalyticsAggregationStatusResponse> ReadAsync(
        DailyMetricsRangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            request.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new AnalyticsCommandException(
                "Analytics.Aggregation",
                "ANALYTICS_AGGREGATION_STATUS_RANGE_INVALID",
                400,
                exception.Message,
                "Correct the aggregation-status request and preserve [from, to) range semantics.");
        }

        var evidence = await operationStore.ReadStatusEvidenceAsync(
            request.FromInclusive,
            request.ToExclusive,
            cancellationToken);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.CompletedDays);
        ValidateLatestRunScope(evidence.LatestRun, request);

        var completedByDate = new Dictionary<DateOnly, AnalyticsAggregateDayReadiness>();
        foreach (var day in evidence.CompletedDays)
        {
            if (day.Date < request.FromInclusive || day.Date >= request.ToExclusive)
            {
                throw CorruptEvidence(
                    "Aggregation status store returned a completed day outside the requested range.");
            }

            if (!completedByDate.TryAdd(day.Date, day))
            {
                throw CorruptEvidence(
                    $"Aggregation status store returned duplicate day '{day.Date:yyyy-MM-dd}'.");
            }
        }

        var missingDates = EnumerateDates(request.FromInclusive, request.ToExclusive)
            .Where(date => !completedByDate.ContainsKey(date))
            .ToArray();
        var readiness = ResolveReadiness(missingDates, evidence.LatestRun);
        return new AnalyticsAggregationStatusResponse(
            request.FromInclusive,
            request.ToExclusive,
            AnalyticsContractMapper.ToContract(readiness),
            missingDates,
            evidence.LatestRun is null
                ? null
                : AnalyticsContractMapper.ToResponse(evidence.LatestRun),
            ResolveUnavailableReason(readiness, evidence.LatestRun));
    }

    private static AggregateReadinessState ResolveReadiness(
        IReadOnlyList<DateOnly> missingDates,
        AnalyticsAggregateRun? latestRun)
    {
        if (latestRun?.State == AnalyticsAggregateRunState.Rebuilding)
        {
            return AggregateReadinessState.Rebuilding;
        }

        if (latestRun?.State == AnalyticsAggregateRunState.Blocked)
        {
            return AggregateReadinessState.Blocked;
        }

        if (latestRun?.State == AnalyticsAggregateRunState.Complete &&
            missingDates.Any(date =>
                date >= latestRun.FromInclusive &&
                date < latestRun.ToExclusive))
        {
            throw CorruptEvidence(
                "Complete aggregation run is missing readiness evidence inside its exact date range.");
        }

        return missingDates.Count == 0
            ? AggregateReadinessState.Complete
            : AggregateReadinessState.Partial;
    }

    private static string? ResolveUnavailableReason(
        AggregateReadinessState readiness,
        AnalyticsAggregateRun? latestRun) => readiness switch
    {
        AggregateReadinessState.Complete => null,
        AggregateReadinessState.Rebuilding => "aggregation-rebuilding",
        AggregateReadinessState.Blocked => latestRun?.FailureCode
            ?? throw CorruptEvidence("Blocked aggregation status has no failure code."),
        AggregateReadinessState.Partial => "aggregation-not-materialized",
        _ => throw CorruptEvidence(
            $"Aggregation status contains unsupported readiness '{readiness}'."),
    };

    private static void ValidateLatestRunScope(
        AnalyticsAggregateRun? latestRun,
        DailyMetricsRangeRequest request)
    {
        if (latestRun is null)
        {
            return;
        }

        if (latestRun.FromInclusive >= request.ToExclusive ||
            latestRun.ToExclusive <= request.FromInclusive)
        {
            throw CorruptEvidence(
                "Aggregation status store returned a latest run outside the requested range.");
        }
    }

    private static IEnumerable<DateOnly> EnumerateDates(
        DateOnly fromInclusive,
        DateOnly toExclusive)
    {
        for (var date = fromInclusive; date < toExclusive; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    private static AnalyticsCommandException CorruptEvidence(string detail) =>
        new(
            "Analytics.Persistence",
            "ANALYTICS_AGGREGATION_STATUS_EVIDENCE_CORRUPT",
            500,
            detail,
            "Stop aggregation-status reads and repair the Analytics aggregation operation ledger.");
}
