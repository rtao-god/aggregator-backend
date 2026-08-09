using Microsoft.Extensions.Hosting;
using Platform.Messaging;

namespace Aggregator.Query.Worker;

/// <summary>Delivers Query-owned public-read activation events from the durable outbox.</summary>
public sealed class QueryOutboxWorker(
    PostgresOutboxDispatcher dispatcher,
    QueryOutboxWorkerOptions options) : BackgroundService
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
