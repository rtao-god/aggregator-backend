using Aggregator.Catalog.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aggregator.Catalog.Worker;

/// <summary>Claims and executes durable Catalog publication operations.</summary>
public sealed class CatalogPublicationOperationWorker(
    IServiceScopeFactory scopeFactory,
    CatalogWorkerOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var executor = scope.ServiceProvider.GetRequiredService<CatalogPublicationOperationExecutor>();
            var processed = await executor.ExecuteNextAsync(
                options.DispatcherIdentity,
                options.PublicationLeaseDuration,
                options.MaximumPublicationAttempts,
                stoppingToken);
            if (!processed)
            {
                await Task.Delay(options.EmptyDelay, stoppingToken);
            }
        }
    }
}
