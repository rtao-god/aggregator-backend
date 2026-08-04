using Aggregator.Ingestion.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ingestion.Infrastructure.Tests;

public sealed class IngestionPersistenceModelTests
{
    [Fact]
    public void BatchModelOwnsConcurrencyAndSemanticUniqueness()
    {
        using var context = CreateContext();
        var batch = FindTable(context.Model, "batches", "import_batch");
        var aggregateRevision = batch.FindProperty("AggregateRevision");

        Assert.NotNull(aggregateRevision);
        Assert.True(aggregateRevision.IsConcurrencyToken);
        Assert.Contains(
            batch.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual(["ProducerIdentity", "CollectorExportId"]));
        var checkNames = batch.GetCheckConstraints()
            .Select(check => check.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ck_import_batch_item_count", checkNames);
        Assert.Contains("ck_import_batch_decision_counts", checkNames);
        Assert.Contains("ck_import_batch_time_order", checkNames);
    }

    [Fact]
    public void IdempotencyResultIsOwnedByScopeAndKey()
    {
        using var context = CreateContext();
        var command = FindTable(context.Model, "operations", "command_idempotency");
        var primaryKey = Assert.Single(command.GetKeys(), key => key.IsPrimaryKey());

        Assert.Equal(
            ["Scope", "Key"],
            primaryKey.Properties.Select(property => property.Name).ToArray());
        var batchForeignKey = Assert.Single(command.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Restrict, batchForeignKey.DeleteBehavior);
    }

    [Fact]
    public void ImmutablePackagePartsNeverCascadeDelete()
    {
        using var context = CreateContext();
        var immutableTables = new[]
        {
            FindTable(context.Model, "batches", "import_batch_manifest"),
            FindTable(context.Model, "batches", "import_batch_source_policy"),
            FindTable(context.Model, "batches", "import_batch_artifact"),
        };

        Assert.All(
            immutableTables.SelectMany(entity => entity.GetForeignKeys()),
            foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
    }

    private static IngestionDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql("Host=localhost;Database=ingestion_db;Username=ingestion_app;Password=test")
            .Options;
        return new IngestionDbContext(options);
    }

    private static IEntityType FindTable(IModel model, string schema, string tableName) =>
        model.GetEntityTypes().Single(entity =>
            string.Equals(entity.GetSchema(), schema, StringComparison.Ordinal) &&
            string.Equals(entity.GetTableName(), tableName, StringComparison.Ordinal));
}
