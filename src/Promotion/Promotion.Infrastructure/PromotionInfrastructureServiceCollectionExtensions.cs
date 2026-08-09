using Aggregator.Promotion.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Promotion.Infrastructure;

public static class PromotionInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddPromotionInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Promotion")
            ?? throw new InvalidOperationException("Connection string 'Promotion' is required.");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Promotion' cannot be empty.");
        }

        services.AddDbContext<PromotionDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddScoped<EfPromotionRepository>();
        services.AddScoped<IPromotionRepository>(services =>
            services.GetRequiredService<EfPromotionRepository>());
        services.AddScoped<IPromotionEligibilityPlacementReconciler>(services =>
            services.GetRequiredService<EfPromotionRepository>());
        services.AddScoped<
            IPromotionEligibilityProjectionStore,
            PostgresPromotionEligibilityProjectionStore>();
        services.AddScoped<PromotionReadinessProbe>();
        services.AddSingleton<IPromotionClock, SystemPromotionClock>();
        services.AddSingleton<IPromotionIdSource, UuidV7PromotionIdSource>();
        return services;
    }
}

public sealed class UuidV7PromotionIdSource : IPromotionIdSource
{
    public Guid CreateId() => Guid.CreateVersion7();
}

public sealed class SystemPromotionClock : IPromotionClock
{
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
}

/// <summary>Performs read-only connectivity proof for the Promotion-owned database.</summary>
public sealed class PromotionReadinessProbe
{
    private readonly PromotionDbContext _dbContext;

    public PromotionReadinessProbe(PromotionDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken) =>
        _dbContext.Database.CanConnectAsync(cancellationToken);
}
