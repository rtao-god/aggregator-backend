using Aggregator.Analytics.Application;
using Aggregator.Analytics.Infrastructure;
using Aggregator.Platform.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aggregator.Analytics.Worker;

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
        var aggregationOptions = builder.Configuration
            .GetSection(AnalyticsAggregationWorkerOptions.SectionName)
            .Get<AnalyticsAggregationWorkerOptions>()
            ?? new AnalyticsAggregationWorkerOptions();
        aggregationOptions.Validate();
        var publicReadOptions = builder.Configuration
            .GetSection(AnalyticsPublicReadProjectionWorkerOptions.SectionName)
            .Get<AnalyticsPublicReadProjectionWorkerOptions>()
            ?? throw new InvalidOperationException(
                $"Configuration section '{AnalyticsPublicReadProjectionWorkerOptions.SectionName}' is required.");
        publicReadOptions.Validate();
        var listingAccessOptions = builder.Configuration
            .GetSection(AnalyticsListingAccessProjectionWorkerOptions.SectionName)
            .Get<AnalyticsListingAccessProjectionWorkerOptions>()
            ?? new AnalyticsListingAccessProjectionWorkerOptions
            {
                BrokerUri = publicReadOptions.BrokerUri,
                Exchange = publicReadOptions.Exchange,
                DeadLetterExchange = publicReadOptions.DeadLetterExchange,
            };
        listingAccessOptions.Validate();

        builder.Services.AddSingleton(aggregationOptions);
        builder.Services.AddSingleton(publicReadOptions);
        builder.Services.AddSingleton(listingAccessOptions);
        builder.Services.AddAnalyticsApplication();
        builder.Services.AddAnalyticsInfrastructure(builder.Configuration);
        builder.Services.AddPlatformObservability(
            builder.Configuration,
            "analytics-worker");
        builder.Services.AddHostedService<AnalyticsAggregationWorker>();
        builder.Services.AddHostedService<AnalyticsPublicReadProjectionWorker>();
        builder.Services.AddHostedService<AnalyticsListingAccessProjectionWorker>();
        return builder.Build();
    }
}

public sealed record AnalyticsAggregationWorkerOptions
{
    public const string SectionName = "Analytics:Aggregation";

    public int LookbackDays { get; init; } = 2;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan FailureDelay { get; init; } = TimeSpan.FromSeconds(10);

    public int MaximumConsecutiveFailures { get; init; } = 5;

    public void Validate()
    {
        if (LookbackDays is < 1 or > 31)
        {
            throw new InvalidOperationException(
                $"{SectionName}:LookbackDays must be between 1 and 31.");
        }

        if (MaximumConsecutiveFailures is < 1 or > 20)
        {
            throw new InvalidOperationException(
                $"{SectionName}:MaximumConsecutiveFailures must be between 1 and 20.");
        }

        ValidateDelay(PollInterval, nameof(PollInterval), TimeSpan.FromSeconds(1), TimeSpan.FromHours(24));
        ValidateDelay(FailureDelay, nameof(FailureDelay), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
    }

    private static void ValidateDelay(
        TimeSpan value,
        string name,
        TimeSpan minimum,
        TimeSpan maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} must be between {minimum} and {maximum}.");
        }
    }
}

internal sealed class AnalyticsAggregationWorker(
    IServiceScopeFactory scopeFactory,
    AnalyticsAggregationWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<AnalyticsAggregationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider
                    .GetRequiredService<RebuildDailyAnalyticsMetricsService>();
                var todayUtc = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
                var result = await service.RebuildAsync(
                    new RebuildDailyAnalyticsMetricsRequest(
                        todayUtc.AddDays(-options.LookbackDays),
                        todayUtc),
                    stoppingToken);
                AnalyticsAggregationWorkerLog.RebuildCompleted(
                    logger,
                    result.MaterializedMetricCount,
                    result.RemovedStaleMetricCount,
                    result.FromInclusive,
                    result.ToExclusive);
                consecutiveFailures = 0;
                await Task.Delay(options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                AnalyticsAggregationWorkerLog.RebuildFailed(
                    logger,
                    exception,
                    consecutiveFailures,
                    options.MaximumConsecutiveFailures);
                if (consecutiveFailures >= options.MaximumConsecutiveFailures)
                {
                    throw;
                }

                await Task.Delay(options.FailureDelay, stoppingToken);
            }
        }
    }
}

internal static partial class AnalyticsAggregationWorkerLog
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Analytics aggregate rebuild materialized {MetricCount} rows and removed {StaleMetricCount} stale rows for [{FromDate}, {ToDate}).")]
    public static partial void RebuildCompleted(
        ILogger logger,
        int metricCount,
        int staleMetricCount,
        DateOnly fromDate,
        DateOnly toDate);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Analytics aggregate rebuild failed ({FailureCount}/{FailureLimit}).")]
    public static partial void RebuildFailed(
        ILogger logger,
        Exception exception,
        int failureCount,
        int failureLimit);
}
