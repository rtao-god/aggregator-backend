using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

public sealed class QueryPublicReadEventReachabilityTests
{
    [Fact]
    public void QueryWorkerUsesTheCanonicalCoordinatedProjectionComposition()
    {
        var repository = RepositoryModel.Load();
        var program = Read(repository, "src/Query/Query.Worker/Program.cs");

        Assert.Contains(".AddQueryProjectionCoordination()", program, StringComparison.Ordinal);
        Assert.Contains(
            "IQueryActivationCheckpointReader, NpgsqlQueryActivationCheckpointReader",
            program,
            StringComparison.Ordinal);
        Assert.Contains("outboxOptions);", program, StringComparison.Ordinal);
        Assert.DoesNotContain(".GetTypes()", program, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "typeof(IQueryProjectionStore).IsAssignableFrom",
            program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void QueryWorkerOwnsARealOutboxDispatcherPath()
    {
        var repository = RepositoryModel.Load();
        var workerProjectPath = Path.Combine(
            repository.Root,
            "src",
            "Query",
            "Query.Worker",
            "Query.Worker.csproj");
        var project = XDocument.Load(workerProjectPath);
        var references = project
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value?.Replace('\\', '/'))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registration = Read(
            repository,
            "src/Query/Query.Worker/QueryWorkerServiceCollectionExtensions.cs");
        var migration = Read(
            repository,
            "src/Query/Query.Migrations/Migrations/V011__query_public_read_outbox.sql");

        Assert.Contains(
            "../../BuildingBlocks/Platform.Messaging/Platform.Messaging.csproj",
            references);
        Assert.Contains("PostgresOutboxDispatcher", registration, StringComparison.Ordinal);
        Assert.Contains("AddHostedService<QueryOutboxWorker>()", registration, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE messaging.outbox_message", migration, StringComparison.Ordinal);
        Assert.Contains("query_outbox_lease_consistent", migration, StringComparison.Ordinal);
        Assert.Contains("query_outbox_dispatch_idx", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPublicReadPointerWriterPublishesTheProducerEvent()
    {
        var repository = RepositoryModel.Load();
        var baseStore = Read(
            repository,
            "src/Query/Query.Infrastructure/NpgsqlQueryProjectionStore.cs");
        var recompositionStore = Read(
            repository,
            "src/Query/Query.Infrastructure/OverlayPreservingQueryProjectionStore.cs");
        var promotionStore = Read(
            repository,
            "src/Query/Query.Infrastructure/PostgresPromotionOverlayProjectionStore.cs");
        var safetyStore = Read(
            repository,
            "src/Query/Query.Infrastructure/PostgresVisibilitySafetyProjectionStore.Operations.cs");

        Assert.Contains("QueryPublicReadActivationOutboxWriter.InsertAsync(", baseStore, StringComparison.Ordinal);
        Assert.Contains("HasPendingPublicationRecompositionAsync(", baseStore, StringComparison.Ordinal);
        Assert.Contains("QueryPublicReadActivationOutboxWriter.InsertAsync(", recompositionStore, StringComparison.Ordinal);
        Assert.Contains("DeletePublicationBlockAsync(", recompositionStore, StringComparison.Ordinal);
        Assert.Contains("QueryPublicReadActivationOutboxWriter.InsertAsync(", promotionStore, StringComparison.Ordinal);
        Assert.Contains("QueryPublicReadActivationOutboxWriter.InsertAsync(", safetyStore, StringComparison.Ordinal);
        Assert.Contains("DeleteBlockAsync(", safetyStore, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeSuppliesVisibilityAndOutboxContractsToQueryWorker()
    {
        var repository = RepositoryModel.Load();
        var compose = Read(repository, "compose.yaml");
        var workerStart = compose.IndexOf("  query-worker:", StringComparison.Ordinal);
        var nextService = compose.IndexOf("\n  ingestion-api:", workerStart, StringComparison.Ordinal);
        Assert.True(workerStart >= 0 && nextService > workerStart, "Query worker service block was not found.");
        var workerBlock = compose[workerStart..nextService];

        Assert.Contains("Query__VisibilityWorker__RoutingKey: catalog.public-visibility-suppression.changed", workerBlock, StringComparison.Ordinal);
        Assert.Contains("Query__Outbox__DispatcherIdentity: query-public-read-outbox", workerBlock, StringComparison.Ordinal);
        Assert.Contains("Query__Outbox__MaximumDeliveryAttempts", workerBlock, StringComparison.Ordinal);
        Assert.Contains("rabbitmq: {condition: service_healthy}", workerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings__Analytics", workerBlock, StringComparison.Ordinal);
    }

    private static string Read(RepositoryModel repository, string relativePath) =>
        File.ReadAllText(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
