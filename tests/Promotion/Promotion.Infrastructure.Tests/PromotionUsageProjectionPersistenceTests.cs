using System.Reflection;
using Aggregator.Promotion.Application;
using Aggregator.Promotion.Infrastructure;

namespace Promotion.Infrastructure.Tests;

public sealed class PromotionUsageProjectionPersistenceTests
{
    [Fact]
    public void UsageProjectionStoreImplementsCanonicalOwnerPort()
    {
        Assert.Contains(
            typeof(IPromotionUsageProjectionStore),
            typeof(PostgresPromotionUsageProjectionStore).GetInterfaces());
    }

    [Fact]
    public void UsageProjectionMigrationsOwnRevisionedInboxAndWindow()
    {
        var initialMigration = ReadRepositoryFile(
            "src/Promotion/Promotion.Migrations/Migrations/V005__analytics_promotion_usage_projection.sql");
        var revisionMigration = ReadRepositoryFile(
            "src/Promotion/Promotion.Migrations/Migrations/V006__analytics_promotion_usage_revisions.sql");

        Assert.Contains(
            "CREATE TABLE analytics_usage_projection.inbox_message",
            initialMigration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE analytics_usage_projection.promotion_usage_window",
            initialMigration,
            StringComparison.Ordinal);
        Assert.Contains(
            "UNIQUE (placement_id, window_starts_at_utc, window_ends_at_utc)",
            initialMigration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE analytics_usage_projection.promotion_usage_window_revision",
            revisionMigration,
            StringComparison.Ordinal);
        Assert.Contains(
            "PRIMARY KEY (usage_window_id, source_aggregate_revision)",
            revisionMigration,
            StringComparison.Ordinal);
        Assert.Contains(
            "NEW.source_aggregate_revision <> OLD.source_aggregate_revision + 1",
            revisionMigration,
            StringComparison.Ordinal);
        Assert.Contains(
            "trg_promotion_usage_current_has_revision",
            revisionMigration,
            StringComparison.Ordinal);
        Assert.Contains(
            "trg_promotion_usage_revision_immutable",
            revisionMigration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "accepted_impressions > 0 OR accepted_listing_opens > 0 OR accepted_outbound_clicks > 0",
            revisionMigration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StoreUsesSerializableTransactionAndExactReplayChecks()
    {
        var source = ReadRepositoryFile(
            "src/Promotion/Promotion.Infrastructure/PostgresPromotionUsageProjectionStore.cs");

        Assert.Contains("IsolationLevel.Serializable", source, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", source, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", source, StringComparison.Ordinal);
        Assert.Contains("PROMOTION_USAGE_INBOX_MESSAGE_CORRUPT", source, StringComparison.Ordinal);
        Assert.Contains("PROMOTION_USAGE_INBOX_ORPHANED", source, StringComparison.Ordinal);
        Assert.Contains("PROMOTION_USAGE_WINDOW_IDENTITY_CONFLICT", source, StringComparison.Ordinal);
        Assert.Contains("PROMOTION_USAGE_REVISION_STALE", source, StringComparison.Ordinal);
        Assert.Contains("PROMOTION_USAGE_REVISION_GAP", source, StringComparison.Ordinal);
        Assert.Contains("isStale ? 409 : 503", source, StringComparison.Ordinal);
        Assert.Contains(
            "$\"New Promotion usage window '{change.Projection.UsageWindowId:D}' must start at source revision 1, but received {change.Projection.SourceAggregateRevision}.\",\n                    503);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("InsertRevisionAsync", source, StringComparison.Ordinal);
        Assert.Contains("UpdateCurrentAsync", source, StringComparison.Ordinal);
        Assert.Contains(
            "FROM analytics_usage_projection.promotion_usage_window_revision",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AnalyticsDbContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog_db", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
