using Aggregator.Ingestion.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        var primaryKey = Assert.Single(command.GetKeys().Where(key => key.IsPrimaryKey()));

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

    [Fact]
    public void InfrastructureRegistrationRequiresDedicatedIngestionConnection()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddIngestionInfrastructure(configuration));

        Assert.Equal("Connection string 'Ingestion' is required.", exception.Message);
    }

    [Fact]
    public void InfrastructureRegistrationProvidesOnlyIngestionOwnerPorts()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ingestion"] =
                    "Host=localhost;Database=ingestion_db;Username=ingestion_app;Password=test",
            })
            .Build();

        services.AddIngestionInfrastructure(configuration);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType.FullName == "Aggregator.Ingestion.Application.IIngestionBatchRepository" &&
            descriptor.ImplementationType == typeof(EfIngestionRepository));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType.FullName == "Aggregator.Ingestion.Application.IIngestionProducerRegistry" &&
            descriptor.ImplementationType == typeof(EfIngestionProducerRegistry));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType.FullName == "Aggregator.Ingestion.Application.ICatalogIngestionReferenceReader" &&
            descriptor.ImplementationType == typeof(EfCatalogIngestionReferenceReader));
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
