using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Promotion.Application;

public static class PromotionApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddPromotionApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<PromotionProductService>();
        services.AddScoped<PromotionEntitlementService>();
        services.AddScoped<PromotionPlacementService>();
        return services;
    }
}
