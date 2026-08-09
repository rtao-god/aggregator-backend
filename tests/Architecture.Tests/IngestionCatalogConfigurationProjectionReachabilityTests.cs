using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

public sealed class IngestionCatalogConfigurationProjectionReachabilityTests
{
    [Fact]
    public void CatalogActivationPublishesTheProducerContractInsideThePointerTransaction()
    {
        var repository = RepositoryModel.Load();
        var contracts = Read(
            repository,
            "src/Catalog/Catalog.Contracts/CatalogIntegrationEvents.cs");
        var service = Read(
            repository,
            "src/Catalog/Catalog.Application/CatalogConfigurationService.cs");
        var ports = Read(
            repository,
            "src/Catalog/Catalog.Application/CatalogApplicationPorts.cs");
        var persistence = Read(
            repository,
            "src/Catalog/Catalog.Infrastructure/EfCatalogRepository.Configuration.cs");
        var controller = Read(
            repository,
            "src/Catalog/Catalog.Api/CatalogConfigurationController.cs");

        Assert.Contains(
            "public const string ConfigurationActivated = \"catalog.configuration.activated\";",
            contracts,
            StringComparison.Ordinal);
        Assert.Contains(
            "public const string ConfigurationActivated = \"aggregator.catalog.configuration-activated@1\";",
            contracts,
            StringComparison.Ordinal);
        Assert.Contains(
            "public sealed record CatalogConfigurationActivated(",
            contracts,
            StringComparison.Ordinal);
        Assert.Contains(
            "CatalogConfigurationActivationOutboxFactory outboxFactory",
            ports,
            StringComparison.Ordinal);
        Assert.Contains(
            "new CatalogConfigurationActivated(",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "CatalogOutboxMessageFactory.Create(",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "await activationRepository.ActivateConfigurationAsync(",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "await ExecuteInTransactionAsync(async innerCancellationToken =>",
            persistence,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddOutbox(outboxFactory(previousConfigurationRevisionId, aggregateRevision));",
            persistence,
            StringComparison.Ordinal);
        Assert.Contains(
            "await _dbContext.SaveChangesAsync(innerCancellationToken);",
            persistence,
            StringComparison.Ordinal);
        Assert.Contains(
            "CatalogEventContextAccessor.Require(correlation)",
            controller,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IngestionConsumesOnlyCatalogContractsAndOwnsItsProjectionLocally()
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
        Assert.Contains(
            "../../Catalog/Catalog.Contracts/Catalog.Contracts.csproj",
            workerReferences);
        Assert.DoesNotContain(
            applicationReferences
                .Concat(infrastructureReferences)
                .Concat(workerReferences),
            reference =>
                reference.Contains("Catalog.Domain", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Catalog.Application", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Catalog.Infrastructure", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Catalog.Api", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Catalog.Worker", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WorkerRegistersTheOnlyProjectionMutationPath()
    {
        var repository = RepositoryModel.Load();
        var workerProgram = Read(repository, "src/Ingestion/Ingestion.Worker/Program.cs");
        var workerComposition = Read(
            repository,
            "src/Ingestion/Ingestion.Worker/IngestionWorkerServiceCollectionExtensions.cs");
        var projectionComposition = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/IngestionCatalogProjectionInfrastructureExtensions.cs");
        var apiProgram = Read(repository, "src/Ingestion/Ingestion.Api/Program.cs");

        Assert.Contains(
            "IngestionCatalogConfigurationProjectionWorkerOptions.SectionName",
            workerProgram,
            StringComparison.Ordinal);
        Assert.Contains(
            ".AddIngestionCatalogProjectionInfrastructure()",
            workerProgram,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddHostedService<IngestionCatalogConfigurationProjectionWorker>()",
            workerComposition,
            StringComparison.Ordinal);
        Assert.Contains(
            "ICatalogConfigurationProjectionStore,",
            projectionComposition,
            StringComparison.Ordinal);
        Assert.Contains(
            "PostgresCatalogConfigurationProjectionStore",
            projectionComposition,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "AddIngestionCatalogProjectionInfrastructure",
            apiProgram,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IngestionCatalogConfigurationProjectionWorker",
            apiProgram,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RabbitMQ", apiProgram, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsumerValidatesEnvelopeDigestIdentityAndBoundedRedelivery()
    {
        var repository = RepositoryModel.Load();
        var worker = Read(
            repository,
            "src/Ingestion/Ingestion.Worker/IngestionCatalogConfigurationProjectionWorker.cs");
        var options = Read(
            repository,
            "src/Ingestion/Ingestion.Worker/IngestionCatalogConfigurationProjectionWorkerOptions.cs");

        Assert.Contains(
            "CatalogIntegrationEventContracts.ConfigurationActivated",
            worker,
            StringComparison.Ordinal);
        Assert.Contains("payload-digest", worker, StringComparison.Ordinal);
        Assert.Contains("VerifyPayloadIntegrity(", worker, StringComparison.Ordinal);
        Assert.Contains("ValidateMessageIdentity(", worker, StringComparison.Ordinal);
        Assert.Contains(
            "JsonUnmappedMemberHandling.Disallow",
            worker,
            StringComparison.Ordinal);
        Assert.Contains("allowIntegerValues: false", worker, StringComparison.Ordinal);
        Assert.Contains("x-queue-type", worker, StringComparison.Ordinal);
        Assert.Contains("x-delivery-limit", worker, StringComparison.Ordinal);
        Assert.Contains("x-dead-letter-exchange", worker, StringComparison.Ordinal);
        Assert.Contains("BasicAckAsync", worker, StringComparison.Ordinal);
        Assert.Contains("BasicNackAsync", worker, StringComparison.Ordinal);
        Assert.Contains("BasicRejectAsync", worker, StringComparison.Ordinal);
        Assert.Contains(
            "exception is IngestionApplicationException { StatusCode: 503 }",
            worker,
            StringComparison.Ordinal);
        Assert.Contains(
            "RoutingKey must be the producer-owned Catalog configuration activation key",
            options,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceOwnsAtomicInboxLineageAndMonotonicProjection()
    {
        var repository = RepositoryModel.Load();
        var migration = Read(
            repository,
            "src/Ingestion/Ingestion.Migrations/Migrations/V007__catalog_configuration_projection_inbox.sql");
        var store = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/PostgresCatalogConfigurationProjectionStore.cs");
        var policy = Read(
            repository,
            "src/Ingestion/Ingestion.Application/CatalogConfigurationProjectionSequencePolicy.cs");
        var reader = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/PostgresCatalogIngestionReferenceReader.cs");

        Assert.Contains(
            "CREATE TABLE messaging.catalog_configuration_inbox",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "UNIQUE (catalog_key, aggregate_revision)",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "REFERENCES messaging.catalog_configuration_inbox(message_id)",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "INGESTION_CATALOG_PROJECTION_REBUILD_REQUIRED",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsolationLevel.Serializable",
            store,
            StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", store, StringComparison.Ordinal);
        Assert.Contains(
            "CatalogConfigurationProjectionSequencePolicy.RequireNext(",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "INSERT INTO messaging.catalog_configuration_inbox",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "INSERT INTO catalog_projection.catalog_reference",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "INGESTION_CATALOG_CONFIGURATION_INBOX_CORRUPT",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "INGESTION_CATALOG_CONFIGURATION_REVISION_GAP",
            policy,
            StringComparison.Ordinal);
        Assert.Contains(
            "INNER JOIN messaging.catalog_configuration_inbox AS inbox",
            reader,
            StringComparison.Ordinal);
        Assert.Contains(
            "CatalogConfigurationProjectionDigest.Compute(",
            reader,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".Order()", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog_db", store, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("catalog_db", reader, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComposeWiresTheConsumerWithoutCatalogDatabaseCredentials()
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
            "Ingestion__CatalogProjection__BrokerUri:",
            workerBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "Ingestion__CatalogProjection__RoutingKey: catalog.configuration.activated",
            workerBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "Ingestion__CatalogProjection__DeadLetterQueue:",
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
        Assert.DoesNotContain("catalog_app", workerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("CATALOG_APP_PASSWORD", workerBlock, StringComparison.Ordinal);
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
