using Aggregator.Analytics.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Analytics.Infrastructure.Tests;

public sealed class AnalyticsAggregateRunPersistenceTests
{
    [Fact]
    public void AggregateRunModelOwnsOneActiveLeaseAndImmutableDayEvidence()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var run = FindTable(model, "aggregates", "aggregate_run");
        var item = FindTable(model, "aggregates", "aggregate_run_item");
        var readiness = FindTable(model, "aggregates", "aggregate_readiness");

        var runCheckNames = run.GetCheckConstraints()
            .Select(check => check.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ck_analytics_aggregate_run_range", runCheckNames);
        Assert.Contains("ck_analytics_aggregate_run_state", runCheckNames);
        Assert.Contains("ck_analytics_aggregate_run_shape", runCheckNames);
        Assert.Contains(
            run.GetIndexes(),
            index => index.IsUnique &&
                string.Equals(
                    index.GetDatabaseName(),
                    "ux_analytics_aggregate_run_rebuilding",
                    StringComparison.Ordinal) &&
                string.Equals(index.GetFilter(), "state = 1", StringComparison.Ordinal));

        var itemForeignKey = Assert.Single(item.GetForeignKeys());
        Assert.Equal(run, itemForeignKey.PrincipalEntityType);
        Assert.Equal(DeleteBehavior.Restrict, itemForeignKey.DeleteBehavior);
        Assert.Equal(
            ["RunId"],
            itemForeignKey.Properties.Select(property => property.Name).ToArray());

        var readinessForeignKey = Assert.Single(readiness.GetForeignKeys());
        Assert.Equal(item, readinessForeignKey.PrincipalEntityType);
        Assert.Equal(DeleteBehavior.Restrict, readinessForeignKey.DeleteBehavior);
        Assert.Equal(
            ["RunId", "MetricDate"],
            readinessForeignKey.Properties.Select(property => property.Name).ToArray());
        Assert.Contains(
            readiness.GetCheckConstraints(),
            check => string.Equals(
                check.Name,
                "ck_analytics_aggregate_readiness_digest",
                StringComparison.Ordinal));
    }

    [Fact]
    public void AggregateRunMigrationEnforcesTerminalAndReadinessMutationGuards()
    {
        var migration = ReadRepositoryFile(
            "src/Analytics/Analytics.Migrations/Migrations/V006__aggregate_run_readiness.sql");

        Assert.Contains(
            "CREATE UNIQUE INDEX ux_analytics_aggregate_run_rebuilding",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TRIGGER trg_analytics_aggregate_run_guard",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "OLD.state <> 1 OR NEW.state NOT IN (2, 3)",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TRIGGER trg_analytics_aggregate_run_item_immutable",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TRIGGER trg_analytics_aggregate_readiness_no_delete",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "FOREIGN KEY (run_id, metric_date)",
            migration,
            StringComparison.Ordinal);
    }

    private static AnalyticsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseNpgsql("Host=localhost;Database=analytics_db;Username=analytics_app;Password=test")
            .Options;
        return new AnalyticsDbContext(options);
    }

    private static IEntityType FindTable(IModel model, string schema, string tableName) =>
        model.GetEntityTypes().Single(entity =>
            string.Equals(entity.GetSchema(), schema, StringComparison.Ordinal) &&
            string.Equals(entity.GetTableName(), tableName, StringComparison.Ordinal));

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Repository file '{relativePath}' was not found from '{AppContext.BaseDirectory}'.");
    }
}
