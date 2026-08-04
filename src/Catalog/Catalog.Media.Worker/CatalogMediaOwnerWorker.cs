using Aggregator.CatalogMedia.Application;
using Microsoft.Extensions.Hosting;
using Platform.Messaging;

namespace Aggregator.CatalogMedia.Worker;

public sealed class CatalogMediaOwnerWorker(
    CatalogMediaProcessingService processing,
    PostgresOutboxDispatcher outbox,
    CatalogMediaWorkerOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = await processing.ProcessOneAsync(
                options.WorkerIdentity,
                options.SystemActorId,
                options.LeaseDuration,
                options.MaximumAttempts,
                stoppingToken);
            var dispatched = await outbox.DispatchOnceAsync(stoppingToken);
            if (!processed && dispatched == 0)
                await Task.Delay(options.EmptyDelay, stoppingToken);
        }
    }
}
