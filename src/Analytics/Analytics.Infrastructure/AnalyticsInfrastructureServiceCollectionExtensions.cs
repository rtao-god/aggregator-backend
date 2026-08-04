using Aggregator.Analytics.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Analytics.Infrastructure;

public static class AnalyticsInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddAnalyticsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Analytics")
            ?? throw new InvalidOperationException("Connection string 'Analytics' is required.");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Analytics' cannot be empty.");
        }

        services.AddDbContext<AnalyticsDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddScoped<EfAnalyticsRepository>();
        services.AddScoped<IAnalyticsEventStore>(services =>
            services.GetRequiredService<EfAnalyticsRepository>());
        services.AddScoped<IPublicReadReferenceStore>(services =>
            services.GetRequiredService<EfAnalyticsRepository>());
        services.AddScoped<IPublicReadReferenceProjectionWriter>(services =>
            services.GetRequiredService<EfAnalyticsRepository>());
        services.AddScoped<IListingMetricsAccessProjectionWriter>(services =>
            services.GetRequiredService<EfAnalyticsRepository>());
        services.AddScoped<IDailyListingMetricsStore>(services =>
            services.GetRequiredService<EfAnalyticsRepository>());
        services.AddScoped<IListingMetricsAuthorizer>(services =>
            services.GetRequiredService<EfAnalyticsRepository>());
        services.AddScoped<AnalyticsReadinessProbe>();
        services.AddSingleton<IAnalyticsIdSource, UuidV7AnalyticsIdSource>();
        return services;
    }
}

public sealed class UuidV7AnalyticsIdSource : IAnalyticsIdSource
{
    public Guid CreateId() => Guid.CreateVersion7();
}

/// <summary>Performs read-only connectivity proof for the Analytics-owned database.</summary>
public sealed class AnalyticsReadinessProbe
{
    private readonly AnalyticsDbContext _dbContext;

    public AnalyticsReadinessProbe(AnalyticsDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken) =>
        _dbContext.Database.CanConnectAsync(cancellationToken);
}
