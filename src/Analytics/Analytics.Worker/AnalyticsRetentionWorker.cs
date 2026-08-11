using Aggregator.Analytics.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aggregator.Analytics.Worker;

/// <summary>Runs aggregate-closed Analytics retention independently of API/read paths.</summary>
internal sealed class AnalyticsRetentionWorker(
    IServiceScopeFactory scopeFactory,
    AnalyticsRetentionWorkerOptions options,
    ILogger<AnalyticsRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<RunAnalyticsRetentionService>();
                var result = await service.RunAsync(
                    TimeSpan.FromDays(options.RawEventRetentionDays),
                    options.BatchSize,
                    stoppingToken);
                AnalyticsRetentionWorkerLog.BatchCompleted(
                    logger,
                    result.OperationId,
                    result.RetainBeforeUtc,
                    result.MinimizedEventCount,
                    result.MayHaveMore);
                consecutiveFailures = 0;
                await Task.Delay(
                    result.MayHaveMore ? options.ContinuationDelay : options.PollInterval,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                AnalyticsRetentionWorkerLog.BatchFailed(
                    logger,
                    exception,
                    consecutiveFailures,
                    options.MaximumConsecutiveFailures);
                if (consecutiveFailures >= options.MaximumConsecutiveFailures)
                {
                    throw;
                }

                await Task.Delay(options.FailureDelay, stoppingToken);
            }
        }
    }
}

internal static partial class AnalyticsRetentionWorkerLog
{
    [LoggerMessage(
        EventId = 2031,
        Level = LogLevel.Information,
        Message = "Analytics retention operation {OperationId} minimized {EventCount} events before {RetainBeforeUtc}; more eligible work: {MayHaveMore}.")]
    public static partial void BatchCompleted(
        ILogger logger,
        Guid operationId,
        DateTimeOffset retainBeforeUtc,
        int eventCount,
        bool mayHaveMore);

    [LoggerMessage(
        EventId = 2032,
        Level = LogLevel.Error,
        Message = "Analytics retention batch failed ({FailureCount}/{FailureLimit}).")]
    public static partial void BatchFailed(
        ILogger logger,
        Exception exception,
        int failureCount,
        int failureLimit);
}
