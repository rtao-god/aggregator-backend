using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Messaging;

namespace Aggregator.Analytics.Worker;

/// <summary>Delivers Analytics-owned Promotion usage events from the durable outbox.</summary>
public sealed class AnalyticsOutboxWorker(
    PostgresOutboxDispatcher dispatcher,
    AnalyticsOutboxWorkerOptions options,
    ILogger<AnalyticsOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = await dispatcher.DispatchOnceAsync(stoppingToken);
                if (dispatched == 0)
                {
                    await Task.Delay(options.EmptyDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AnalyticsOutboxWorkerLog.DispatchFailed(
                    logger,
                    exception,
                    options.EmptyDelay);
                await Task.Delay(options.EmptyDelay, stoppingToken);
            }
        }
    }
}

internal static partial class AnalyticsOutboxWorkerLog
{
    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Error,
        Message = "Analytics outbox dispatch failed after durable failure recording. Retrying after {RetryDelay}.")]
    public static partial void DispatchFailed(
        ILogger logger,
        Exception exception,
        TimeSpan retryDelay);
}
