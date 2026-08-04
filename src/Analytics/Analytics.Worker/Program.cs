using Aggregator.Analytics.Application;
using Aggregator.Analytics.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Observability;

namespace Aggregator.Analytics.Worker;

public partial class Program
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
        var options = builder.Configuration
            .GetSection(AnalyticsAggregationWorkerOptions.SectionName)
            .Get<AnalyticsAggregationWorkerOptions>()
            ?? new AnalyticsAggregationWorkerOptions();
        options.Validate();

        builder.Services.AddSingleton(options);
        builder.Services.AddAnalyticsRuntimeApplication();
        builder.Services.AddAnalyticsRuntimeInfrastructure(builder.Configuration);
        builder.Services.AddPlatformObservability(
            builder.Configuration,
            "analytics-aggregation-worker");
        builder.Services.AddHostedService<AnalyticsAggregationWorker>();
        return builder.Build();
    }
}

public sealed record AnalyticsAggregationWorkerOptions
{
    public const string SectionName = "Analytics:Aggregation";

    public int BatchSize { get; init; } = 1_000;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan FailureDelay { get; init; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        if (BatchSize is < 1 or > 10_000)
        {
            throw new InvalidOperationException(
                $"{SectionName}:BatchSize must be between 1 and 10000.");
        }

        ValidateDelay(PollInterval, nameof(PollInterval));
        ValidateDelay(FailureDelay, nameof(FailureDelay));
    }

    private static void ValidateDelay(TimeSpan value, string name)
    {
        if (value < TimeSpan.FromMilliseconds(100) || value > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} must be between 100 milliseconds and 5 minutes.");
        }
    }
}

internal sealed class AnalyticsAggregationWorker(
    IServiceScopeFactory scopeFactory,
    AnalyticsAggregationWorkerOptions options,
    ILogger<AnalyticsAggregationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider
                    .GetRequiredService<AggregateAnalyticsObservationsService>();
                var processed = await service.AggregateAsync(
                    options.BatchSize,
                    stoppingToken);
                if (processed > 0)
                {
                    logger.LogInformation(
                        "Aggregated {ObservationCount} Analytics observations.",
                        processed);
                    continue;
                }

                await Task.Delay(options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Analytics aggregation iteration failed; retrying after the bounded failure delay.");
                await Task.Delay(options.FailureDelay, stoppingToken);
            }
        }
    }
}
