using Microsoft.Extensions.Hosting;
using Platform.Messaging;

namespace Aggregator.Analytics.Worker;

/// <summary>Delivers Analytics-owned Promotion usage events from the durable outbox.</summary>
public sealed class AnalyticsOutboxWorker(
    PostgresOutboxDispatcher dispatcher,
    AnalyticsOutboxWorkerOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var dispatched = await dispatcher.DispatchOnceAsync(stoppingToken);
            if (dispatched == 0)
            {
                await Task.Delay(options.EmptyDelay, stoppingToken);
            }
        }
    }
}
