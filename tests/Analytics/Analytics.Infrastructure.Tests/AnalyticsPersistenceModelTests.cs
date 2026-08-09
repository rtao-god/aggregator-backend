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
        var campaignParameter = FindTable(
            model,
            "events",
            "interaction_event_campaign_parameter");

        Assert.Contains(
            interactionEvent.GetIndexes(),
            index => index.IsUnique &&
                string.Equals(
                    index.GetDatabaseName(),
                    "ux_analytics_interaction_event_semantic_key",
                    StringComparison.Ordinal) &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual(["ClientEventId", "EventKind"]));
        var checkNames = interactionEvent.GetCheckConstraints()
            .Select(check => check.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ck_analytics_interaction_event_listing_shape", checkNames);
        Assert.Contains("ck_analytics_interaction_event_placement_shape", checkNames);
        Assert.Contains("ck_analytics_interaction_event_time_bounds", checkNames);
        Assert.Contains("ck_analytics_interaction_event_digest", checkNames);
        var campaignForeignKey = Assert.Single(campaignParameter.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Restrict, campaignForeignKey.DeleteBehavior);
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
    public void QueryActivationsOwnMonotonicCheckpointInboxAndSponsoredMembership()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var publicRead = FindTable(
            model,
            "access_projection",
            "public_read_reference");
        var publicListing = FindTable(
            model,
            "access_projection",
            "public_listing_reference");
        var sponsoredPlacement = FindTable(
            model,
            "access_projection",
            "public_sponsored_placement_reference");
        var checkpoint = FindTable(
            model,
            "access_projection",
            "public_read_activation_checkpoint");
        var inbox = FindTable(model, "messaging", "inbox_message");
        var interactionEvent = FindTable(model, "events", "interaction_event");

        Assert.Contains(
            publicRead.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual(["CatalogKey", "ActivationRevision"]));
        Assert.Contains(
            publicRead.GetCheckConstraints(),
            check => string.Equals(
                check.Name,
                "ck_analytics_public_read_projection_digest",
                StringComparison.Ordinal));

        Assert.Contains(
            sponsoredPlacement.GetForeignKeys(),
            foreignKey =>
                foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual(["PublicReadRevisionId", "ListingId"]) &&
                foreignKey.PrincipalEntityType == publicListing &&
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Contains(
            sponsoredPlacement.GetForeignKeys(),
            foreignKey =>
                foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual(["PublicReadRevisionId"]) &&
                foreignKey.PrincipalEntityType == publicRead &&
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict);

        Assert.Contains(
            interactionEvent.GetForeignKeys(),
            foreignKey =>
                foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual(["PublicReadRevisionId", "PlacementId", "ListingId"]) &&
                foreignKey.PrincipalEntityType == sponsoredPlacement &&
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict);

        var checkpointRevision = checkpoint.FindProperty("ActivationRevision");
        Assert.NotNull(checkpointRevision);
        Assert.True(checkpointRevision.IsConcurrencyToken);
        Assert.Contains(
            checkpoint.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType == publicRead &&
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict);

        Assert.Contains(
            inbox.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType == publicRead &&
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
        var inboxCheckNames = inbox.GetCheckConstraints()
            .Select(check => check.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ck_analytics_inbox_payload_digest", inboxCheckNames);
        Assert.Contains("ck_analytics_inbox_activation_revision", inboxCheckNames);
        Assert.Contains("ck_analytics_inbox_result_digest", inboxCheckNames);
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
