using Aggregator.Analytics.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Analytics.Infrastructure.Tests;

public sealed class AnalyticsPersistenceModelTests
{
    [Fact]
    public void InteractionEventsOwnSemanticIdempotencyAndBoundedContext()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var interactionEvent = FindTable(model, "events", "interaction_event");

        Assert.Contains(
            interactionEvent.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual(["ClientEventId", "EventKind"]));
        var checkNames = interactionEvent.GetCheckConstraints()
            .Select(check => check.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ck_analytics_interaction_event_listing_shape", checkNames);
        Assert.Contains("ck_analytics_interaction_event_placement_shape", checkNames);
        Assert.Contains("ck_analytics_interaction_event_time_bounds", checkNames);
        Assert.Contains("ck_analytics_interaction_event_digest", checkNames);
    }

    [Fact]
    public void PublicAndAccessProjectionsRemainLocalAndRevisioned()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var publicListing = FindTable(
            model,
            "access_projection",
            "public_listing_reference");
        var access = FindTable(
            model,
            "access_projection",
            "listing_access_projection");

        var publicReadForeignKey = Assert.Single(publicListing.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Restrict, publicReadForeignKey.DeleteBehavior);
        var sourceRevision = access.FindProperty("SourceAggregateRevision");
        Assert.NotNull(sourceRevision);
        Assert.True(sourceRevision.IsConcurrencyToken);
        Assert.Contains(
            access.GetCheckConstraints(),
            check => string.Equals(
                check.Name,
                "ck_analytics_listing_access_revision",
                StringComparison.Ordinal));
    }

    [Fact]
    public void IncompleteMetricsCannotCarryFabricatedCounts()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var metrics = FindTable(model, "aggregates", "daily_listing_metric");
        var checkNames = metrics.GetCheckConstraints()
            .Select(check => check.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ck_analytics_daily_metric_value_shape", checkNames);
        Assert.Contains("ck_analytics_daily_metric_nonnegative", checkNames);
        Assert.Contains("ck_analytics_daily_metric_readiness", checkNames);
    }

    [Fact]
    public void AnalyticsDomainStorageContainsNoRawIpField()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var properties = model.GetEntityTypes().SelectMany(entity => entity.GetProperties()).ToArray();

        Assert.DoesNotContain(
            properties,
            property => property.Name is "RawIp" or "IpAddress" or "RemoteIpAddress");
        Assert.DoesNotContain(
            properties,
            property => string.Equals(property.GetColumnType(), "inet", StringComparison.OrdinalIgnoreCase));
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
}
