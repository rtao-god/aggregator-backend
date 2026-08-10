using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Messaging;

namespace Aggregator.Query.Worker;

/// <summary>Delivers Query-owned public-read activation events from the durable outbox.</summary>
public sealed class QueryOutboxWorker(
    PostgresOutboxDispatcher dispatcher,
    QueryOutboxWorkerOptions options,
    ILogger<QueryOutboxWorker> logger) : BackgroundService
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
            catch (Exception exception) when (
                OutboxDispatchFailurePolicy.IsRecoverable(exception))
            {
                QueryOutboxWorkerLog.DispatchFailed(
                    logger,
                    exception,
                    options.EmptyDelay);
                await Task.Delay(options.EmptyDelay, stoppingToken);
            }
        }
    }
}

internal static partial class QueryOutboxWorkerLog
{
    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Error,
        Message = "Query outbox dispatch failed; durable state remains authoritative. Retrying after {RetryDelay}.")]
    public static partial void DispatchFailed(
        ILogger logger,
        Exception exception,
        TimeSpan retryDelay);
}
