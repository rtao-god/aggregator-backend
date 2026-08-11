using Aggregator.Analytics.Infrastructure;

namespace Analytics.Infrastructure.Tests;

public sealed class AnalyticsRetentionPersistenceTests
{
    [Fact]
    public void RetentionClaimsOnlyAggregateClosedRawEventsAndPreservesOwnerMeaning()
    {
        var migration = ReadRepositoryFile(
            "src/Analytics/Analytics.Migrations/Migrations/V008__interaction_event_retention.sql");
        var store = ReadRepositoryFile(
            "src/Analytics/Analytics.Infrastructure/PostgresAnalyticsRetentionStore.cs");

        Assert.Contains(
            "ADD COLUMN retention_state smallint NOT NULL DEFAULT 1",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "ck_analytics_interaction_event_retention_shape",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "trg_analytics_retention_operation_immutable",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "trg_analytics_interaction_event_retention_guard",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "trg_analytics_retention_audit_immutable",
            migration,
            StringComparison.Ordinal);

        Assert.Contains(
            "INNER JOIN aggregates.aggregate_readiness AS readiness",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE interaction.retention_state = 1",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "FOR UPDATE OF interaction SKIP LOCKED",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "DELETE FROM events.interaction_event_campaign_parameter",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "SET placement_scope_key = NULL",
            store,
            StringComparison.Ordinal);
        Assert.DoesNotContain("page_context", store, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AggregateProjectionExposesOnlyRetainedCountAndIdentityFields()
    {
        var projectionType = typeof(AnalyticsDbContext).Assembly.GetType(
            "Aggregator.Analytics.Infrastructure.AnalyticsAggregateInteractionProjection",
            throwOnError: true)!;
        var propertyNames = projectionType
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "CatalogKey",
            "EventKind",
            "Id",
            "ListingId",
            "OccurredAtUtc",
            "PayloadDigest",
            "PlacementExposureKind",
            "PlacementId",
            "PublicReadRevisionId",
            "QualityState",
        ],
            propertyNames);
        Assert.DoesNotContain("PageContext", propertyNames);
        Assert.DoesNotContain("PlacementScopeKey", propertyNames);
        Assert.DoesNotContain("CampaignParameters", propertyNames);
        Assert.DoesNotContain("RetentionState", propertyNames);
    }

    [Fact]
    public void AggregateAndPromotionUsageOwnersCannotReadMinimizableRawContext()
    {
        var aggregateWriter = ReadRepositoryFile(
            "src/Analytics/Analytics.Infrastructure/EfAnalyticsAggregateWriter.cs");
        var usageMaterializer = ReadRepositoryFile(
            "src/Analytics/Analytics.Infrastructure/AnalyticsPromotionUsageMaterializer.cs");

        Assert.Contains(
            "new AnalyticsAggregateInteractionProjection(",
            aggregateWriter,
            StringComparison.Ordinal);
        Assert.Contains(
            "IReadOnlyList<AnalyticsAggregateInteractionProjection> eventRows",
            usageMaterializer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AnalyticsInteractionEventRow[]", aggregateWriter, StringComparison.Ordinal);
        Assert.DoesNotContain("AnalyticsInteractionEventRow", usageMaterializer, StringComparison.Ordinal);
        Assert.DoesNotContain("PageContext", aggregateWriter, StringComparison.Ordinal);
        Assert.DoesNotContain("PlacementScopeKey", aggregateWriter, StringComparison.Ordinal);
        Assert.DoesNotContain("InteractionCampaignParameters", aggregateWriter, StringComparison.Ordinal);
        Assert.DoesNotContain("RetentionState", aggregateWriter, StringComparison.Ordinal);
        Assert.DoesNotContain("PageContext", usageMaterializer, StringComparison.Ordinal);
        Assert.DoesNotContain("PlacementScopeKey", usageMaterializer, StringComparison.Ordinal);
        Assert.DoesNotContain("CampaignParameters", usageMaterializer, StringComparison.Ordinal);
    }

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
