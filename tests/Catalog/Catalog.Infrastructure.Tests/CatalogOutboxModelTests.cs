using Aggregator.Catalog.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Catalog.Infrastructure.Tests;

public sealed class CatalogOutboxModelTests
{
    [Fact]
    public void OutboxPayloadUsesExactTextStorage()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var outbox = model.GetEntityTypes().Single(entity =>
            string.Equals(entity.GetSchema(), "catalog", StringComparison.Ordinal) &&
            string.Equals(entity.GetTableName(), "outbox_message", StringComparison.Ordinal));
        var payload = outbox.FindProperty("PayloadJson")
            ?? throw new InvalidOperationException("Catalog outbox payload property is missing.");

        Assert.Equal("text", payload.GetColumnType());
    }

    private static CatalogDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=catalog_db;Username=catalog_app;Password=test")
            .Options;
        return new CatalogDbContext(options);
    }
}
