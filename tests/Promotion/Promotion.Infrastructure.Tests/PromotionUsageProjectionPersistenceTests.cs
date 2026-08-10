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
    public void UsageProjectionMigrationOwnsImmutableInboxAndWindow()
    {
        var migration = ReadRepositoryFile(
            "src/Promotion/Promotion.Migrations/Migrations/V005__analytics_promotion_usage_projection.sql");

        Assert.Contains(
            "CREATE TABLE analytics_usage_projection.inbox_message",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE analytics_usage_projection.promotion_usage_window",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "UNIQUE (placement_id, window_starts_at_utc, window_ends_at_utc)",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "source_message_id uuid NOT NULL UNIQUE",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "accepted_impressions > 0 OR accepted_listing_opens > 0 OR accepted_outbound_clicks > 0",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "trg_promotion_usage_inbox_immutable",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "trg_promotion_usage_window_immutable",
            migration,
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
