using Aggregator.CatalogMedia.Application;
using Aggregator.CatalogMedia.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Catalog.Media.Infrastructure.Tests;

public sealed class CatalogMediaInfrastructureBoundaryTests
{
    [Fact]
    public void RepositoryImplementsTheCatalogMediaOwnerPort()
    {
        Assert.Contains(
            typeof(ICatalogMediaRepository),
            typeof(EfCatalogMediaRepository).GetInterfaces());
    }

    [Fact]
    public void InfrastructureDoesNotReferenceAnotherBusinessContextImplementation()
    {
        var references = typeof(EfCatalogMediaRepository).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(reference => reference.StartsWith("Catalog.", StringComparison.Ordinal) ||
                reference.StartsWith("Query.", StringComparison.Ordinal) ||
                reference.StartsWith("Ingestion.", StringComparison.Ordinal) ||
                reference.StartsWith("Analytics.", StringComparison.Ordinal) ||
                reference.StartsWith("Promotion.", StringComparison.Ordinal))
            .ToArray();

        Assert.All(
            references,
            reference => Assert.StartsWith("Catalog.Media.", reference, StringComparison.Ordinal));
    }

    [Fact]
    public void OutboxPayloadUsesExactTextStorage()
    {
        var options = new DbContextOptionsBuilder<CatalogMediaDbContext>()
            .UseNpgsql("Host=localhost;Database=catalog_db;Username=catalog_app;Password=test")
            .Options;
        using var context = new CatalogMediaDbContext(options);
        var model = context.GetService<IDesignTimeModel>().Model;
        var outbox = model.GetEntityTypes().Single(entity =>
            string.Equals(entity.GetSchema(), "media_messaging", StringComparison.Ordinal) &&
            string.Equals(entity.GetTableName(), "outbox_message", StringComparison.Ordinal));
        var payload = outbox.FindProperty("PayloadJson")
            ?? throw new InvalidOperationException("Catalog Media outbox payload property is missing.");

        Assert.Equal("text", payload.GetColumnType());
    }
}
