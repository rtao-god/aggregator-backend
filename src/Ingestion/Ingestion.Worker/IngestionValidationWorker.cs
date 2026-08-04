using Aggregator.Ingestion.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aggregator.Ingestion.Worker;

/// <summary>Executes bounded validation leases against the canonical Ingestion processing ledger.</summary>
public sealed class IngestionValidationWorker(
    IServiceScopeFactory scopeFactory,
    IngestionWorkerOptions options,
    ILogger<IngestionValidationWorker> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly IngestionWorkerOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<IngestionValidationWorker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                for (; processed < _options.ValidationBatchSize; processed++)
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var service = scope.ServiceProvider
                        .GetRequiredService<ValidateIngestionPackageService>();
                    if (!await service.ProcessNextAsync(
                            _options.WorkerIdentity,
                            _options.LeaseDuration,
                            stoppingToken))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
          exception,
          "Ingestion validation worker {WorkerIdentity} failed its current owner batch; persisted leases remain authoritative",
          _options.WorkerIdentity);
            }

            if (processed == 0)
            {
                await Task.Delay(_options.EmptyDelay, stoppingToken);
            }
        }
    }
}
