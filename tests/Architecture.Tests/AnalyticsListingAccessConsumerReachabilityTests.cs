using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

public sealed class AnalyticsListingAccessConsumerReachabilityTests
{
    [Fact]
    public void AnalyticsConsumesOnlyTheProducerOwnedCatalogAccessContract()
    {
        var repository = RepositoryModel.Load();
        var applicationReferences = ReadProjectReferences(
            repository,
            "src/Analytics/Analytics.Application/Analytics.Application.csproj");
        var workerReferences = ReadProjectReferences(
            repository,
            "src/Analytics/Analytics.Worker/Analytics.Worker.csproj");

        Assert.Contains(
            "../../Catalog/Catalog.Contracts/Catalog.Contracts.csproj",
            applicationReferences);
        Assert.Contains(
            "../../Catalog/Catalog.Contracts/Catalog.Contracts.csproj",
            workerReferences);
        Assert.Contains(
            "../Analytics.Application/Analytics.Application.csproj",
            workerReferences);
        Assert.Contains(
            "../Analytics.Infrastructure/Analytics.Infrastructure.csproj",
            workerReferences);
        Assert.DoesNotContain(
            applicationReferences.Concat(workerReferences),
            reference =>
                reference.Contains("Catalog.Application", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Catalog.Domain", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Catalog.Infrastructure", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Catalog.Api", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyticsWorkerRegistersTheCatalogAccessProjectionConsumer()
    {
        var repository = RepositoryModel.Load();
        var program = Read(repository, "src/Analytics/Analytics.Worker/Program.cs");
        var worker = Read(
            repository,
            "src/Analytics/Analytics.Worker/AnalyticsListingAccessProjectionWorker.cs");
        var infrastructure = Read(
            repository,
            "src/Analytics/Analytics.Infrastructure/AnalyticsInfrastructureServiceCollectionExtensions.cs");

        Assert.Contains(
            "AddHostedService<AnalyticsListingAccessProjectionWorker>()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "AnalyticsListingAccessProjectionWorkerOptions.SectionName",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "BrokerUri = publicReadOptions.BrokerUri",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyCatalogListingAccessGrantChangedService",
            worker,
            StringComparison.Ordinal);
        Assert.Contains(
            "CatalogIntegrationEventContracts.ListingAccessGrantChanged",
            worker,
            StringComparison.Ordinal);
        Assert.Contains("payload-digest", worker, StringComparison.Ordinal);
        Assert.Contains("causation-id", worker, StringComparison.Ordinal);
        Assert.Contains("VerifyPayloadIntegrity", worker, StringComparison.Ordinal);
        Assert.Contains("ValidateMessageIdentity", worker, StringComparison.Ordinal);
        Assert.Contains("BasicAckAsync", worker, StringComparison.Ordinal);
        Assert.Contains("BasicNackAsync", worker, StringComparison.Ordinal);
        Assert.Contains("x-queue-type", worker, StringComparison.Ordinal);
        Assert.Contains("x-delivery-limit", worker, StringComparison.Ordinal);
        Assert.Contains(
            "IListingMetricsAccessProjectionStore,",
            infrastructure,
            StringComparison.Ordinal);
        Assert.Contains(
            "EfListingMetricsAccessProjectionStore",
            infrastructure,
            StringComparison.Ordinal);
        Assert.Contains(
            "IListingMetricsAuthorizer, EfListingMetricsAuthorizer",
            infrastructure,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("Catalog.Api", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyticsPersistenceOwnsGrantInboxRevisionAndFailClosedAuthorization()
    {
        var repository = RepositoryModel.Load();
        var migration = Read(
            repository,
            "src/Analytics/Analytics.Migrations/Migrations/V005__catalog_listing_access_projection.sql");
        var store = Read(
            repository,
            "src/Analytics/Analytics.Infrastructure/EfListingMetricsAccessProjectionStore.cs");
        var authorizer = Read(
            repository,
            "src/Analytics/Analytics.Infrastructure/EfListingMetricsAuthorizer.cs");
        var metricsService = Read(
            repository,
            "src/Analytics/Analytics.Application/ReadDailyListingMetricsService.cs");

        Assert.Contains(
            "IF EXISTS (SELECT 1 FROM access_projection.listing_access_projection)",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE access_projection.listing_access_grant_projection",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE messaging.listing_access_grant_inbox",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "fk_analytics_access_grant_inbox_grant",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "source_aggregate_revision >= 2",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "can_view_analytics = false",
            migration,
            StringComparison.Ordinal);

        Assert.Contains("IsolationLevel.Serializable", store, StringComparison.Ordinal);
        Assert.Equal(2, Count(store, "pg_advisory_xact_lock"));
        Assert.Contains(
            "ANALYTICS_ACCESS_INBOX_MESSAGE_CONFLICT",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "ANALYTICS_ACCESS_REVISION_GAP",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "ListingMetricsAccessProjectionDisposition.IgnoredStale",
            store,
            StringComparison.Ordinal);
        Assert.Contains("row.ActorId == actorId", authorizer, StringComparison.Ordinal);
        Assert.Contains("row.ListingId == listingId", authorizer, StringComparison.Ordinal);
        Assert.Contains("row.CanViewAnalytics", authorizer, StringComparison.Ordinal);
        Assert.Contains("row.RevokedAtUtc == null", authorizer, StringComparison.Ordinal);
        Assert.Contains("row.ExpiresAtUtc > nowUtc", authorizer, StringComparison.Ordinal);

        var authorizationOffset = metricsService.IndexOf(
            "await authorizer.AuthorizeAsync(",
            StringComparison.Ordinal);
        var metricsReadOffset = metricsService.IndexOf(
            "await metricsStore.GetRangeAsync(",
            StringComparison.Ordinal);
        Assert.True(
            authorizationOffset >= 0 && metricsReadOffset > authorizationOffset,
            "Metrics authorization must complete before aggregate rows are read.");
        Assert.DoesNotContain("HttpClient", authorizer, StringComparison.Ordinal);
        Assert.DoesNotContain("Catalog.Contracts", metricsService, StringComparison.Ordinal);
        Assert.DoesNotContain("Catalog.Api", metricsService, StringComparison.Ordinal);
        Assert.DoesNotContain("ICatalog", metricsService, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyAccessProjectionWriterIsStructurallyAbsent()
    {
        var repository = RepositoryModel.Load();
        var analyticsSources = Directory
            .EnumerateFiles(
                Path.Combine(repository.Root, "src", "Analytics"),
                "*.cs",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        Assert.DoesNotContain(
            analyticsSources,
            source => source.Contains(
                "IListingMetricsAccessProjectionWriter",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            analyticsSources,
            source => source.Contains(
                "AnalyticsListingAccessProjectionRow",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            analyticsSources,
            source => source.Contains(
                "ToTable(\"listing_access_projection\"",
                StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyticsWorkerHasBrokerAccessButNoCatalogDatabaseCredential()
    {
        var repository = RepositoryModel.Load();
        var compose = Read(repository, "compose.yaml");
        var workerStart = compose.IndexOf("  analytics-worker:", StringComparison.Ordinal);
        var nextService = compose.IndexOf("\n  promotion-api:", workerStart, StringComparison.Ordinal);
        Assert.True(
            workerStart >= 0 && nextService > workerStart,
            "Analytics worker service block was not found.");
        var workerBlock = compose[workerStart..nextService];

        Assert.Contains(
            "Analytics__PublicReadProjection__BrokerUri:",
            workerBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "rabbitmq: {condition: service_healthy}",
            workerBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConnectionStrings__Catalog",
            workerBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Catalog__", workerBlock, StringComparison.Ordinal);
    }

    private static int Count(string value, string marker)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += marker.Length;
        }

        return count;
    }

    private static HashSet<string> ReadProjectReferences(
        RepositoryModel repository,
        string relativePath)
    {
        var project = XDocument.Load(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return project
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value?.Replace('\\', '/'))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Read(RepositoryModel repository, string relativePath) =>
        File.ReadAllText(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
