using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Promotion.Application;

/// <summary>Registers only the Analytics usage consumer application boundary.</summary>
public static class PromotionUsageProjectionApplicationExtensions
{
    public static IServiceCollection AddPromotionUsageProjectionApplication(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ApplyAnalyticsPromotionUsageWindowService>();
        return services;
    }
}
