using Aggregator.Catalog.Application;
using Aggregator.Catalog.Media.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.ObjectStorage;

namespace Aggregator.Catalog.Media.Infrastructure;

public static class CatalogMediaInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddCatalogMediaInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Catalog")
            ?? throw new InvalidOperationException("Connection string 'Catalog' is required for Catalog media.");
        services.AddDbContext<CatalogMediaDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<EfCatalogMediaRepository>();
        services.AddScoped<ICatalogMediaRepository>(services =>
            services.GetRequiredService<EfCatalogMediaRepository>());
        services.AddScoped<ICatalogMediaBindingAuthority, CatalogMediaBindingAuthority>();
        services.AddScoped<ICatalogMediaObjectStore, ObjectStoreCatalogMediaStore>();
        services.AddSingleton<ICatalogMediaClock, SystemCatalogMediaClock>();
        services.AddSingleton<ICatalogMediaIdSource, UuidV7CatalogMediaIdSource>();
        services.AddScoped<CatalogMediaReadinessProbe>();
        return services;
    }
}

public sealed class SystemCatalogMediaClock : ICatalogMediaClock
{
    public DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
}

public sealed class UuidV7CatalogMediaIdSource : ICatalogMediaIdSource
{
    public Guid CreateId() => Guid.CreateVersion7();
}

public sealed class CatalogMediaReadinessProbe(CatalogMediaDbContext dbContext)
{
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken) =>
        dbContext.Database.CanConnectAsync(cancellationToken);
}
