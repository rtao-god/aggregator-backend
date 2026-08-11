using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aggregator.Analytics.Application;

/// <summary>One atomic Analytics-owned retention batch over aggregate-closed interaction events.</summary>
public sealed record AnalyticsRetentionBatch(
    Guid OperationId,
    DateTimeOffset RetainBeforeUtc,
    int MaximumEvents,
    string RequestDigest);

/// <summary>Durable result of one exact retention batch.</summary>
public sealed record AnalyticsRetentionBatchResult(
    Guid OperationId,
    DateTimeOffset RetainBeforeUtc,
    int MinimizedEventCount,
    bool MayHaveMore);

/// <summary>Persists one aggregate-closed retention batch atomically.</summary>
public interface IAnalyticsRetentionStore
{
    Task<AnalyticsRetentionBatchResult> MinimizeAsync(
        AnalyticsRetentionBatch batch,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Runs bounded raw-event minimization without exposing retention through read paths.</summary>
public sealed class RunAnalyticsRetentionService(
    IAnalyticsRetentionStore store,
    IAnalyticsIdSource idSource,
    TimeProvider timeProvider)
{
    public async Task<AnalyticsRetentionBatchResult> RunAsync(
        TimeSpan retentionAge,
        int maximumEvents,
        CancellationToken cancellationToken)
    {
        if (retentionAge < TimeSpan.FromDays(1) || retentionAge > TimeSpan.FromDays(3650))
        {
            throw Failure(
                "ANALYTICS_RETENTION_AGE_INVALID",
                "Analytics raw-event retention age must be between 1 and 3650 days.",
                "Configure an explicit bounded Analytics retention age before starting the worker.");
        }

        if (maximumEvents is < 1 or > 5000)
        {
            throw Failure(
                "ANALYTICS_RETENTION_BATCH_SIZE_INVALID",
                "Analytics retention batch size must be between 1 and 5000 events.",
                "Configure a bounded Analytics retention batch size between 1 and 5000.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "ANALYTICS_RETENTION_CLOCK_NOT_UTC",
                "Analytics retention clock must return UTC timestamps.",
                "Repair the Analytics worker clock configuration before retention resumes.");
        }

        var retainBeforeUtc = nowUtc - retentionAge;
        var operationId = idSource.CreateId();
        if (operationId == Guid.Empty)
        {
            throw Failure(
                "ANALYTICS_RETENTION_OPERATION_ID_INVALID",
                "Analytics retention operation identity cannot be empty.",
                "Repair the Analytics ID source before retention resumes.");
        }

        var requestDigest = ComputeRequestDigest(retainBeforeUtc, maximumEvents);
        return await store.MinimizeAsync(
            new AnalyticsRetentionBatch(
                operationId,
                retainBeforeUtc,
                maximumEvents,
                requestDigest),
            nowUtc,
            cancellationToken);
    }

    internal static string ComputeRequestDigest(
        DateTimeOffset retainBeforeUtc,
        int maximumEvents)
    {
        if (retainBeforeUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "ANALYTICS_RETENTION_CUTOFF_NOT_UTC",
                "Analytics retention cutoff must be UTC.",
                "Normalize the owner clock to UTC before creating retention work.");
        }

        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"analytics-retention|{retainBeforeUtc:O}|{maximumEvents}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static AnalyticsCommandException Failure(
        string code,
        string detail,
        string requiredAction) =>
        new(
            "Analytics.Retention",
            code,
            500,
            detail,
            requiredAction);
}
