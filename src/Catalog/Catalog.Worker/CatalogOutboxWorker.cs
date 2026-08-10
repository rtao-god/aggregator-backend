using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Messaging;

namespace Aggregator.Catalog.Worker;

/// <summary>Delivers Catalog integration events from the durable owner outbox.</summary>
public sealed class CatalogOutboxWorker(
    PostgresOutboxDispatcher dispatcher,
    CatalogWorkerOptions options,
    ILogger<CatalogOutboxWorker> logger) : BackgroundService
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
                CatalogOutboxWorkerLog.DispatchFailed(
                    logger,
                    exception,
                    options.EmptyDelay);
                await Task.Delay(options.EmptyDelay, stoppingToken);
            }
        }
    }
}

internal static partial class CatalogOutboxWorkerLog
{
    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Error,
        Message = "Catalog outbox dispatch failed; durable state remains authoritative. Retrying after {RetryDelay}.")]
    public static partial void DispatchFailed(
        ILogger logger,
        Exception exception,
        TimeSpan retryDelay);
}
