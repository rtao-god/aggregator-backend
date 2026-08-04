using Aggregator.Promotion.Application;
using Aggregator.Promotion.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Messaging;

namespace Aggregator.Promotion.Worker;

/// <summary>
/// Advances owner-scheduled state and dispatches only events already committed to the Promotion outbox.
/// </summary>
public sealed class PromotionOwnerWorker(
    IServiceScopeFactory scopeFactory,
    PostgresOutboxDispatcher outboxDispatcher,
    PromotionWorkerOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var transitions = await SynchronizeDueAsync(stoppingToken);
            var dispatched = await outboxDispatcher.DispatchOnceAsync(stoppingToken);
            if (transitions == 0 && dispatched == 0)
            {
                await Task.Delay(options.PollDelay, stoppingToken);
            }
        }
    }

    private async Task<int> SynchronizeDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<EfPromotionRepository>();
        var idSource = scope.ServiceProvider.GetRequiredService<IPromotionIdSource>();
        return await repository.SynchronizeDueAsync(
            TimeProvider.System.GetUtcNow(),
            options.SystemActorId,
            options.TransitionBatchSize,
            idSource,
            cancellationToken);
    }
}
