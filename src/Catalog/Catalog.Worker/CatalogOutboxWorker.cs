using Microsoft.Extensions.Hosting;
using Platform.Messaging;

namespace Aggregator.Catalog.Worker;

/// <summary>Delivers Catalog integration events from the durable owner outbox.</summary>
public sealed class CatalogOutboxWorker(
    PostgresOutboxDispatcher dispatcher,
    CatalogWorkerOptions options) : BackgroundService
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
