using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Analytics.Application;

public static class AnalyticsApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddAnalyticsApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<SubmitInteractionEventService>();
        services.AddScoped<SubmitInteractionEventBatchService>();
        services.AddScoped<ApplyPublicReadRevisionActivationService>();
        services.AddScoped<ApplyCatalogListingAccessGrantChangedService>();
        services.AddScoped<ReadListingMetricsRangeService>();
        services.AddScoped<ReadDailyListingMetricsService>();
        services.AddScoped<ReadListingMetricsSummaryService>();
        services.AddScoped<RebuildDailyAnalyticsMetricsService>();
        services.AddScoped<ReadAnalyticsAggregationStatusService>();
        services.AddScoped<RunAnalyticsRetentionService>();
        return services;
    }
}
