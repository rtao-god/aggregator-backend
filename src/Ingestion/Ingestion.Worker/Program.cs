using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Infrastructure;
using Platform.Observability;

namespace Aggregator.Ingestion.Worker;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var host = CreateHost(args);
        await host.RunAsync();
    }

    public static IHost CreateHost(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = Host.CreateApplicationBuilder(args);
        var runtime = IngestionPackageWorkerRuntimeOptions.Read(builder.Configuration);
        var processing = IngestionPackageWorkerRuntimeOptions.ReadProcessing(builder.Configuration);
        runtime.Validate();
        processing.Validate();

        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton(processing);
        builder.Services.AddIngestionApplication();
        builder.Services.AddIngestionInfrastructure(builder.Configuration);
        builder.Services.AddScoped<IngestionPackagePayloadValidator>();
        builder.Services.AddScoped<IngestionPackageProcessingService>();
        builder.Services.AddHostedService<IngestionPackageWorkerService>();
        builder.Services.AddPlatformObservability(
            builder.Configuration,
            "ingestion-package-worker");
        return builder.Build();
    }
}

public sealed record IngestionPackageWorkerRuntimeOptions
{
    public const string SectionName = "Ingestion:PackageWorker";

    public required string WorkerIdentity { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkerIdentity) ||
            WorkerIdentity.Length > 200 ||
            WorkerIdentity.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Configuration '{SectionName}:WorkerIdentity' must be a stable non-empty identity of at most 200 characters.");
        }
    }

    public static IngestionPackageWorkerRuntimeOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetRequiredSection(SectionName);
        return new IngestionPackageWorkerRuntimeOptions
        {
            WorkerIdentity = section[nameof(WorkerIdentity)]
                ?? throw new InvalidOperationException(
                    $"Configuration '{SectionName}:{nameof(WorkerIdentity)}' is required."),
        };
    }

    public static IngestionPackageProcessingOptions ReadProcessing(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Ingestion:PackageProcessing");
        return new IngestionPackageProcessingOptions
        {
            BatchSize = section.GetValue<int?>(nameof(IngestionPackageProcessingOptions.BatchSize)) ?? 10,
            MaximumPayloadBytes = section.GetValue<long?>(nameof(IngestionPackageProcessingOptions.MaximumPayloadBytes))
                ?? 64L * 1024 * 1024,
            LeaseLifetime = section.GetValue<TimeSpan?>(nameof(IngestionPackageProcessingOptions.LeaseLifetime))
                ?? TimeSpan.FromMinutes(5),
            EmptyPollDelay = section.GetValue<TimeSpan?>(nameof(IngestionPackageProcessingOptions.EmptyPollDelay))
                ?? TimeSpan.FromSeconds(2),
        };
    }
}

public sealed class IngestionPackageWorkerService(
    IServiceScopeFactory scopeFactory,
    IngestionPackageWorkerRuntimeOptions runtime,
    IngestionPackageProcessingOptions processing,
    ILogger<IngestionPackageWorkerService> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly IngestionPackageWorkerRuntimeOptions _runtime =
        runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly IngestionPackageProcessingOptions _processing =
        processing ?? throw new ArgumentNullException(nameof(processing));
    private readonly ILogger<IngestionPackageWorkerService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _runtime.Validate();
        _processing.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                for (; processed < _processing.BatchSize; processed++)
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<IngestionPackageProcessingService>();
                    var result = await service.ProcessNextAsync(
                        _runtime.WorkerIdentity,
                        stoppingToken);
                    if (result.Outcome == IngestionPackageProcessOutcome.NoWork)
                    {
                        break;
                    }

                    _logger.LogInformation(
                        "Ingestion package batch {BatchId} completed with outcome {Outcome} and failure code {FailureCode}",
                        result.BatchId,
                        result.Outcome,
                        result.FailureCode);
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
                    "Ingestion package worker {WorkerIdentity} failed its current durable claim; the claim may be reclaimed only after lease expiry",
                    _runtime.WorkerIdentity);
            }

            if (processed == 0)
            {
                await Task.Delay(_processing.EmptyPollDelay, stoppingToken);
            }
        }
    }
}
