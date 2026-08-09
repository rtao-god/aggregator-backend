using Aggregator.Ingestion.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aggregator.Ingestion.Worker;

/// <summary>Delivers durable Ingestion-owned Catalog commands through bounded exact leases.</summary>
public sealed class IngestionCatalogDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IngestionWorkerOptions options,
    ILogger<IngestionCatalogDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider
                    .GetRequiredService<ProcessIngestionCatalogDeliveriesService>();
                processed = await service.ProcessAsync(
                    options.CatalogDeliveryWorkerIdentity,
                    options.CatalogDeliveryBatchSize,
                    options.CatalogDeliveryLeaseDuration,
                    options.CatalogDeliveryMaximumAttempts,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Ingestion Catalog delivery worker {WorkerIdentity} failed its current owner batch; durable leases and outcomes remain authoritative",
                    options.CatalogDeliveryWorkerIdentity);
            }

            if (processed == 0)
            {
                await Task.Delay(options.CatalogDeliveryEmptyDelay, stoppingToken);
            }
        }
    }
}
