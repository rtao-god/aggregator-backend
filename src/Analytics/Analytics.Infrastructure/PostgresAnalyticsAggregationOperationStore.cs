using System.Data;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Analytics.Infrastructure;

internal sealed class PostgresAnalyticsAggregationOperationStore(
    AnalyticsDbContext dbContext) : IAnalyticsAggregationOperationStore
{
    private const string ActiveRunConstraint =
        "ux_analytics_aggregate_run_rebuilding";

    public async Task<AnalyticsAggregationLease> BeginAsync(
        Guid runId,
        Guid leaseToken,
        RebuildDailyAnalyticsMetricsRequest request,
        DateTimeOffset startedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        AnalyticsDomainRules.RequireIdentifier(runId, nameof(runId));
        AnalyticsDomainRules.RequireIdentifier(leaseToken, nameof(leaseToken));
        AnalyticsDomainRules.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        AnalyticsDomainRules.RequireUtc(leaseExpiresAtUtc, nameof(leaseExpiresAtUtc));
        if (request.ToExclusive <= request.FromInclusive || leaseExpiresAtUtc <= startedAtUtc)
        {
            throw InvalidOperation(
                "ANALYTICS_AGGREGATION_LEASE_INVALID",
                "Aggregation lease range or expiry violates the owner contract.",
                "Correct the aggregation request and lease policy before starting work.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var active = await dbContext.AggregateRuns
            .FromSqlRaw(
                "SELECT * FROM aggregates.aggregate_run WHERE state = 1 FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (active is not null)
        {
            if (active.LeaseExpiresAtUtc is not { } activeLeaseExpiry)
            {
                throw PersistenceCorruption(
                    "ANALYTICS_AGGREGATE_ACTIVE_LEASE_MISSING",
                    "Active aggregation run has no lease expiry.");
            }

            if (activeLeaseExpiry > startedAtUtc)
            {
                throw new AnalyticsCommandException(
                    "Analytics.Aggregation",
                    "ANALYTICS_AGGREGATION_ALREADY_RUNNING",
                    409,
                    "Another Analytics aggregation run owns the active execution lease.",
                    "Wait for the active run to finish or for its exact lease to expire.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["activeRunId"] = active.Id,
                        ["leaseExpiresAtUtc"] = activeLeaseExpiry,
                    });
            }

            active.State = (int)AnalyticsAggregateRunState.Blocked;
            active.CompletedAtUtc = startedAtUtc;
            active.LeaseToken = null;
            active.LeaseExpiresAtUtc = null;
            active.FailureCode = "ANALYTICS_AGGREGATION_LEASE_EXPIRED";
            active.FailureDetail =
                "The previous Analytics aggregation process ended without a terminal owner result.";
            active.RequiredAction =
                "Inspect the interrupted worker run and start a new exact aggregation operation.";
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        dbContext.AggregateRuns.Add(new AnalyticsAggregateRunRow
        {
            Id = runId,
            FromInclusive = request.FromInclusive,
            ToExclusive = request.ToExclusive,
            State = (int)AnalyticsAggregateRunState.Rebuilding,
            StartedAtUtc = startedAtUtc,
            LeaseToken = leaseToken,
            LeaseExpiresAtUtc = leaseExpiresAtUtc,
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsActiveRunConflict(exception))
        {
            throw new AnalyticsCommandException(
                "Analytics.Aggregation",
                "ANALYTICS_AGGREGATION_ALREADY_RUNNING",
                409,
                "Another Analytics aggregation run acquired the execution lease concurrently.",
                "Wait for the active run to finish before retrying the same closed date range.");
        }

        return new AnalyticsAggregationLease(
            runId,
            leaseToken,
            request.FromInclusive,
            request.ToExclusive,
            startedAtUtc,
            leaseExpiresAtUtc);
    }

    public async Task MarkBlockedAsync(
        AnalyticsAggregationLease lease,
        AnalyticsAggregationFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(failure);
        ValidateLease(lease);
        AnalyticsDomainRules.RequireUtc(failure.FailedAtUtc, nameof(failure.FailedAtUtc));
        var failureCode = RequireText(failure.Code, nameof(failure.Code), 160);
        var failureDetail = RequireText(failure.Detail, nameof(failure.Detail), 2000);
        var requiredAction = RequireText(failure.RequiredAction, nameof(failure.RequiredAction), 2000);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var row = await dbContext.AggregateRuns
            .FromSqlInterpolated($"""
                SELECT *
                FROM aggregates.aggregate_run
                WHERE id = {lease.RunId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw PersistenceCorruption(
                "ANALYTICS_AGGREGATE_RUN_MISSING",
                $"Aggregation run '{lease.RunId:D}' cannot be found while recording failure.");
        EnsureActiveLease(row, lease);
        row.State = (int)AnalyticsAggregateRunState.Blocked;
        row.CompletedAtUtc = failure.FailedAtUtc;
        row.LeaseToken = null;
        row.LeaseExpiresAtUtc = null;
        row.FailureCode = failureCode;
        row.FailureDetail = failureDetail;
        row.RequiredAction = requiredAction;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AnalyticsAggregationStatusEvidence> ReadStatusEvidenceAsync(
        DateOnly fromInclusive,
        DateOnly toExclusive,
        CancellationToken cancellationToken)
    {
        if (toExclusive <= fromInclusive)
        {
            throw InvalidOperation(
                "ANALYTICS_AGGREGATION_STATUS_RANGE_INVALID",
                "Aggregation status range must be non-empty.",
                "Correct the [from, to) date range before reading status.");
        }

        var readinessRows = await dbContext.AggregateReadiness
            .AsNoTracking()
            .Where(row => row.MetricDate >= fromInclusive && row.MetricDate < toExclusive)
            .OrderBy(row => row.MetricDate)
            .ToArrayAsync(cancellationToken);
        var latestRunRow = await dbContext.AggregateRuns
            .AsNoTracking()
            .Where(row => row.FromInclusive < toExclusive && row.ToExclusive > fromInclusive)
            .OrderByDescending(row => row.StartedAtUtc)
            .ThenByDescending(row => row.Id)
            .FirstOrDefaultAsync(cancellationToken);
        try
        {
            var completedDays = readinessRows
                .Select(row => AnalyticsAggregateDayReadiness.Create(
                    row.MetricDate,
                    row.RunId,
                    row.SourceDigest,
                    row.MetricCount,
                    row.CompletedAtUtc))
                .ToArray();
            return new AnalyticsAggregationStatusEvidence(
                completedDays,
                latestRunRow is null ? null : RestoreRun(latestRunRow));
        }
        catch (AnalyticsDomainException exception)
        {
            throw new AnalyticsCommandException(
                "Analytics.Persistence",
                "ANALYTICS_AGGREGATION_STATUS_ROW_CORRUPT",
                500,
                $"Persisted aggregation status violates its owner contract: {exception.Message}",
                "Stop aggregation-status reads and repair the exact persisted run/readiness rows.");
        }
    }

    private static AnalyticsAggregateRun RestoreRun(AnalyticsAggregateRunRow row) =>
        AnalyticsAggregateRun.Restore(
            row.Id,
            row.FromInclusive,
            row.ToExclusive,
            (AnalyticsAggregateRunState)row.State,
            row.StartedAtUtc,
            row.CompletedAtUtc,
            row.SourceDigest,
            row.MaterializedMetricCount,
            row.RemovedStaleMetricCount,
            row.MaterializedDayCount,
            row.FailureCode,
            row.FailureDetail,
            row.RequiredAction);

    internal static void EnsureActiveLease(
        AnalyticsAggregateRunRow row,
        AnalyticsAggregationLease lease)
    {
        if (row.State != (int)AnalyticsAggregateRunState.Rebuilding ||
            row.LeaseToken != lease.LeaseToken ||
            row.FromInclusive != lease.FromInclusive ||
            row.ToExclusive != lease.ToExclusive ||
            row.StartedAtUtc != lease.StartedAtUtc ||
            row.LeaseExpiresAtUtc != lease.LeaseExpiresAtUtc)
        {
            throw new AnalyticsCommandException(
                "Analytics.Aggregation",
                "ANALYTICS_AGGREGATION_LEASE_STALE",
                409,
                "Aggregation completion or failure does not own the active run lease.",
                "Discard the stale result and reload the current aggregation operation.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["runId"] = lease.RunId,
                    ["persistedState"] = row.State,
                    ["persistedLeaseToken"] = row.LeaseToken,
                });
        }
    }

    private static void ValidateLease(AnalyticsAggregationLease lease)
    {
        AnalyticsDomainRules.RequireIdentifier(lease.RunId, nameof(lease.RunId));
        AnalyticsDomainRules.RequireIdentifier(lease.LeaseToken, nameof(lease.LeaseToken));
        AnalyticsDomainRules.RequireUtc(lease.StartedAtUtc, nameof(lease.StartedAtUtc));
        AnalyticsDomainRules.RequireUtc(lease.LeaseExpiresAtUtc, nameof(lease.LeaseExpiresAtUtc));
        if (lease.ToExclusive <= lease.FromInclusive ||
            lease.LeaseExpiresAtUtc <= lease.StartedAtUtc)
        {
            throw InvalidOperation(
                "ANALYTICS_AGGREGATION_LEASE_INVALID",
                "Aggregation lease violates its owner range or time contract.",
                "Discard the invalid lease and start a new owner operation.");
        }
    }

    private static string RequireText(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw InvalidOperation(
                "ANALYTICS_AGGREGATION_FAILURE_INVALID",
                $"'{name}' must contain between 1 and {maximumLength} characters.",
                "Record a bounded owner failure before completing the aggregation operation.");
        }

        return value.Trim();
    }

    private static bool IsActiveRunConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ActiveRunConstraint,
        };

    private static AnalyticsCommandException InvalidOperation(
        string code,
        string detail,
        string requiredAction) =>
        new(
            "Analytics.Aggregation",
            code,
            500,
            detail,
            requiredAction);

    private static AnalyticsCommandException PersistenceCorruption(
        string code,
        string detail) =>
        new(
            "Analytics.Persistence",
            code,
            500,
            detail,
            "Stop aggregation and repair the Analytics aggregate-run persistence invariant.");
}
