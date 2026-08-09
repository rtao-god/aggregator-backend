using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

public sealed class IngestionCatalogDeliveryReachabilityTests
{
    [Fact]
    public void IngestionConsumesOnlyTheProducerOwnedCatalogContract()
    {
        var repository = RepositoryModel.Load();
        var applicationReferences = ReadProjectReferences(
            repository,
            "src/Ingestion/Ingestion.Application/Ingestion.Application.csproj");
        var infrastructureReferences = ReadProjectReferences(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/Ingestion.Infrastructure.csproj");
        var workerReferences = ReadProjectReferences(
            repository,
            "src/Ingestion/Ingestion.Worker/Ingestion.Worker.csproj");

        Assert.Contains(
            "../../Catalog/Catalog.Contracts/Catalog.Contracts.csproj",
            applicationReferences);
        Assert.Contains(
            "../../Catalog/Catalog.Contracts/Catalog.Contracts.csproj",
            infrastructureReferences);
        Assert.DoesNotContain(
            applicationReferences.Concat(infrastructureReferences),
            IsForbiddenCatalogReference);
        Assert.DoesNotContain(workerReferences, IsAnyCatalogReference);
    }

    [Fact]
    public void WorkerRegistersTheCanonicalLeaseSafeDeliveryPath()
    {
        var repository = RepositoryModel.Load();
        var program = Read(repository, "src/Ingestion/Ingestion.Worker/Program.cs");
        var workerComposition = Read(
            repository,
            "src/Ingestion/Ingestion.Worker/IngestionWorkerServiceCollectionExtensions.cs");
        var applicationComposition = Read(
            repository,
            "src/Ingestion/Ingestion.Application/IngestionApplicationServiceCollectionExtensions.cs");
        var infrastructureComposition = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/IngestionCatalogDeliveryInfrastructureExtensions.cs");

        Assert.Contains("AddIngestionApplication()", program, StringComparison.Ordinal);
        Assert.Contains(
            "AddIngestionCatalogDeliveryInfrastructure(builder.Configuration)",
            program,
            StringComparison.Ordinal);
        Assert.Contains("AddIngestionWorker(options)", program, StringComparison.Ordinal);
        Assert.Contains(
            "AddHostedService<IngestionCatalogDeliveryWorker>()",
            workerComposition,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddScoped<ProcessIngestionCatalogDeliveriesService>()",
            applicationComposition,
            StringComparison.Ordinal);
        Assert.Contains(
            "IIngestionCatalogCommandClient, IngestionCatalogCommandClient",
            infrastructureComposition,
            StringComparison.Ordinal);
        Assert.Contains(
            "IIngestionCatalogDeliveryStore, PostgresIngestionCatalogDeliveryStore",
            infrastructureComposition,
            StringComparison.Ordinal);
        Assert.Contains(
            "IIngestionCatalogDeliveryFailureClassifier",
            infrastructureComposition,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApiRegistersReadOnlyLedgerWithoutCatalogCommandAdapter()
    {
        var repository = RepositoryModel.Load();
        var program = Read(repository, "src/Ingestion/Ingestion.Api/Program.cs");
        var queryComposition = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/IngestionCatalogDeliveryQueryInfrastructureExtensions.cs");
        var controller = Read(
            repository,
            "src/Ingestion/Ingestion.Api/IngestionProcessingController.cs");

        Assert.Contains(
            "AddIngestionCatalogDeliveryQueryInfrastructure()",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddIngestionCatalogDeliveryInfrastructure(",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "IIngestionCatalogDeliveryReader, PostgresIngestionCatalogDeliveryReader",
            queryComposition,
            StringComparison.Ordinal);
        Assert.Contains(
            "[HttpGet(\"deliveries\", Name = IngestionProcessingOperationIds.ReadDeliveries)]",
            controller,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Authorize(Policy = IngestionAuthorizationPolicies.Read)]",
            controller,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IIngestionCatalogCommandClient",
            controller,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ObsoletePublisherAndProcessingStoreDeliveryPathsAreAbsent()
    {
        var repository = RepositoryModel.Load();
        var source = ReadAllCSharp(repository, "src/Ingestion");

        Assert.DoesNotContain(
            "DeliverIngestionCatalogCommandsService",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IIngestionCatalogCommandPublisher",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LeaseCatalogDeliveriesAsync",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RecordCatalogOutcomeAsync",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RemoveAll<DeliverIngestionCatalogCommandsService>",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryClaimRequiresCommittingBatchAndRetainsPoisonCommandsAsTerminalFailures()
    {
        var repository = RepositoryModel.Load();
        var store = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/PostgresIngestionCatalogDeliveryStore.cs");
        var persistence = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/PostgresIngestionCatalogDeliveryStore.Persistence.cs");

        Assert.Contains(
            "INNER JOIN batches.import_batch b ON b.id = d.batch_id",
            store,
            StringComparison.Ordinal);
        Assert.Contains("WHERE b.state = @committing", store, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE OF d SKIP LOCKED", store, StringComparison.Ordinal);
        Assert.Contains(
            "catch (IngestionApplicationException exception)",
            store,
            StringComparison.Ordinal);
        Assert.Contains("RejectCorruptDeliveryAsync(", store, StringComparison.Ordinal);
        Assert.Contains(
            "SET state = 4,",
            persistence,
            StringComparison.Ordinal);
        Assert.Contains(
            "failure_detail = @failure_detail",
            persistence,
            StringComparison.Ordinal);
        Assert.Contains(
            "FinalizeBatchIfTerminalAsync(",
            persistence,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryPersistenceExposesLeaseStateInsteadOfLegacyPublishedState()
    {
        var repository = RepositoryModel.Load();
        var persistence = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/IngestionProcessingPersistence.cs");
        var leaseMigration = Read(
            repository,
            "src/Ingestion/Ingestion.Migrations/Migrations/V005__catalog_delivery_lease_and_retry.sql");
        var consistencyMigration = Read(
            repository,
            "src/Ingestion/Ingestion.Migrations/Migrations/V006__catalog_delivery_state_consistency.sql");

        Assert.Contains("2 => \"leased\"", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("2 => \"published\"", persistence, StringComparison.Ordinal);
        Assert.Contains("state = 2", leaseMigration, StringComparison.Ordinal);
        Assert.Contains("lease_token IS NOT NULL", leaseMigration, StringComparison.Ordinal);
        Assert.Contains(
            "ck_ingestion_catalog_delivery_pending_shape",
            consistencyMigration,
            StringComparison.Ordinal);
        Assert.Contains(
            "INGESTION_CATALOG_DELIVERY_RECOVERED_LEASE",
            consistencyMigration,
            StringComparison.Ordinal);
        Assert.Contains(
            "next_attempt_at_utc > last_changed_at_utc",
            consistencyMigration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeWiresCatalogCommandsWithoutCatalogDatabaseCredentials()
    {
        var repository = RepositoryModel.Load();
        var compose = Read(repository, "compose.yaml");
        var workerStart = compose.IndexOf("  ingestion-worker:", StringComparison.Ordinal);
        var nextService = compose.IndexOf("\n  analytics-api:", workerStart, StringComparison.Ordinal);
        Assert.True(
            workerStart >= 0 && nextService > workerStart,
            "Ingestion worker service block was not found.");
        var workerBlock = compose[workerStart..nextService];

        Assert.Contains(
            "Ingestion__CatalogCommand__BaseAddress: http://catalog-api:8080/",
            workerBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "Ingestion__CatalogCommand__TokenEndpoint:",
            workerBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "Ingestion__CatalogCommand__ClientId:",
            workerBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "Ingestion__CatalogCommand__ClientSecret:",
            workerBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "Ingestion__CatalogCommand__Scope:",
            workerBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "catalog-api: {condition: service_healthy}",
            workerBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConnectionStrings__Catalog",
            workerBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain("catalog_app", workerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("CATALOG_APP_PASSWORD", workerBlock, StringComparison.Ordinal);
    }

    private static bool IsForbiddenCatalogReference(string reference) =>
        reference.Contains("Catalog.Domain", StringComparison.OrdinalIgnoreCase) ||
        reference.Contains("Catalog.Application", StringComparison.OrdinalIgnoreCase) ||
        reference.Contains("Catalog.Infrastructure", StringComparison.OrdinalIgnoreCase) ||
        reference.Contains("Catalog.Api", StringComparison.OrdinalIgnoreCase) ||
        reference.Contains("Catalog.Worker", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnyCatalogReference(string reference) =>
        reference.Contains("/Catalog/", StringComparison.OrdinalIgnoreCase) ||
        reference.Contains("\\Catalog\\", StringComparison.OrdinalIgnoreCase) ||
        reference.Contains("Catalog.", StringComparison.OrdinalIgnoreCase);

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

    private static string ReadAllCSharp(RepositoryModel repository, string relativeDirectory)
    {
        var directory = Path.Combine(
            repository.Root,
            relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        return string.Join(
            '\n',
            Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }

    private static string Read(RepositoryModel repository, string relativePath) =>
        File.ReadAllText(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
