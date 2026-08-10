using Aggregator.Promotion.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Promotion.Infrastructure;

public static class PromotionUsageProjectionInfrastructureExtensions
{
    public static IServiceCollection AddPromotionUsageProjectionInfrastructure(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IPromotionUsageProjectionStore, PostgresPromotionUsageProjectionStore>();
        return services;
    }
}
