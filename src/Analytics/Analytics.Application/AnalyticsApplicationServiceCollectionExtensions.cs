using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Analytics.Application;

public static class AnalyticsApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddAnalyticsApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<SubmitInteractionEventService>();
        services.AddScoped<ApplyPublicReadRevisionActivationService>();
        services.AddScoped<ApplyCatalogListingAccessGrantChangedService>();
        services.AddScoped<ReadDailyListingMetricsService>();
        services.AddScoped<RebuildDailyAnalyticsMetricsService>();
        return services;
    }
}
