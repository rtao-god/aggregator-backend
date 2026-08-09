using Xunit;

namespace Architecture.Tests;

public sealed class IngestionProducerRegistrationReachabilityTests
{
    [Fact]
    public void BatchRegistrationConsumesOnlyTheRevisionedProducerOwner()
    {
        var repository = RepositoryModel.Load();
        var registration = Read(
            repository,
            "src/Ingestion/Ingestion.Application/RegisterIngestionBatchService.cs");
        var composition = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/IngestionInfrastructureServiceCollectionExtensions.cs");
        var registry = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/IngestionProducerRegistry.cs");
        var rows = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/IngestionPersistenceRows.cs");
        var context = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/IngestionDbContext.cs");

        Assert.Contains("IIngestionProducerRegistry producerRegistry", registration, StringComparison.Ordinal);
        Assert.Contains("producerRegistry.GetAsync(", registration, StringComparison.Ordinal);
        Assert.Contains(
            "IIngestionProducerRegistry, IngestionProducerRegistry",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "IIngestionProducerRegistrationStore,",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "PostgresIngestionProducerRegistrationStore",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "IIngestionProducerRegistrationStore store",
            registry,
            StringComparison.Ordinal);
        Assert.DoesNotContain("EfIngestionProducerRegistry", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("IngestionProducerRow", rows, StringComparison.Ordinal);
        Assert.DoesNotContain("DbSet<IngestionProducerRow>", context, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureProducer(", context, StringComparison.Ordinal);
        Assert.DoesNotContain(
            repository.Files,
            file => string.Equals(
                Path.GetRelativePath(repository.Root, file).Replace('\\', '/'),
                "src/Ingestion/Ingestion.Infrastructure/EfIngestionReferenceReaders.cs",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ProducerRegistrationApiOwnsRevisionAndIdempotencyInputs()
    {
        var repository = RepositoryModel.Load();
        var contract = Read(
            repository,
            "src/Ingestion/Ingestion.Contracts/IngestionProducerRegistrationContracts.cs");
        var controller = Read(
            repository,
            "src/Ingestion/Ingestion.Api/IngestionProducerRegistrationsController.cs");
        var policies = Read(
            repository,
            "src/Ingestion/Ingestion.Api/IngestionAuthorizationPolicies.cs");
        var application = Read(
            repository,
            "src/Ingestion/Ingestion.Application/IngestionProducerRegistration.cs");

        Assert.Contains("string ProducerIdentity,", contract, StringComparison.Ordinal);
        Assert.Contains("long ExpectedAggregateRevision,", contract, StringComparison.Ordinal);
        Assert.Contains("bool Active,", contract, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<int> SupportedContractRevisions,", contract, StringComparison.Ordinal);
        Assert.Contains("[HttpPut(Name = \"PutIngestionProducerRegistration\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(Name = \"GetIngestionProducerRegistration\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[FromHeader(Name = \"Idempotency-Key\")]", controller, StringComparison.Ordinal);
        Assert.Contains("IngestionServiceIdentityAccessor.Require(HttpContext)", controller, StringComparison.Ordinal);
        Assert.Contains("IngestionAuthorizationPolicies.ManageProducers", controller, StringComparison.Ordinal);
        Assert.Contains("public const string ManageProducers = \"ingestion.manage-producers\";", policies, StringComparison.Ordinal);
        Assert.Contains("ExpectedAggregateRevision", application, StringComparison.Ordinal);
        Assert.Contains("ComputeContentDigest(", application, StringComparison.Ordinal);
        Assert.Contains("AggregatorCandidateIngestionContract.Revision", application, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSqlStoreOwnsRevisionHistoryAndExactReplay()
    {
        var repository = RepositoryModel.Load();
        var migration = Read(
            repository,
            "src/Ingestion/Ingestion.Migrations/Migrations/V008__producer_registration_owner.sql");
        var core = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/PostgresIngestionProducerRegistrationStore.Core.cs");
        var write = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/PostgresIngestionProducerRegistrationStore.Write.cs");
        var validation = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/PostgresIngestionProducerRegistrationStore.Validation.cs");

        Assert.Contains("IF EXISTS (SELECT 1 FROM contracts.producer_registration)", migration, StringComparison.Ordinal);
        Assert.Contains("producer_registration contains legacy rows", migration, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE contracts.producer_registration_revision", migration, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE operations.producer_registration_command", migration, StringComparison.Ordinal);
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", migration, StringComparison.Ordinal);
        Assert.Contains("trg_producer_registration_revision_immutable", migration, StringComparison.Ordinal);
        Assert.Contains("trg_producer_registration_command_immutable", migration, StringComparison.Ordinal);

        Assert.Contains("IsolationLevel.Serializable", core, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", core, StringComparison.Ordinal);
        Assert.Contains("ReadCommandAsync(", core, StringComparison.Ordinal);
        Assert.Contains("EnsureExpectedRevision(", core, StringComparison.Ordinal);
        Assert.Contains("InsertRevisionAsync(", core, StringComparison.Ordinal);
        Assert.Contains("UpsertCurrentAsync(", core, StringComparison.Ordinal);
        Assert.Contains("InsertCommandAsync(", core, StringComparison.Ordinal);
        Assert.Contains("INGESTION_PRODUCER_IDEMPOTENCY_CONFLICT", validation, StringComparison.Ordinal);
        Assert.Contains("INGESTION_PRODUCER_COMMAND_CORRUPT", validation, StringComparison.Ordinal);
        Assert.Contains("ComputeContentDigest(", validation, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO contracts." + "producer_registration_revision", write, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO operations.producer_registration_command", write, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualProducerRegistrationWritesRemainUnreachable()
    {
        var repository = RepositoryModel.Load();
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "src/Ingestion/Ingestion.Infrastructure/PostgresIngestionProducerRegistrationStore.Write.cs",
            "src/Ingestion/Ingestion.Migrations/Migrations/V008__producer_registration_owner.sql",
        };
        var prohibited = repository.Files
            .Where(file =>
            {
                var relative = Path.GetRelativePath(repository.Root, file).Replace('\\', '/');
                return !allowed.Contains(relative) &&
                    (relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                     relative.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ||
                     relative.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
                     relative.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ||
                     relative.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                     relative.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));
            })
            .Where(file => File.ReadAllText(file).Contains(
                "INSERT INTO contracts." + "producer_registration",
                StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetRelativePath(repository.Root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(prohibited);
    }

    private static string Read(RepositoryModel repository, string relativePath) =>
        File.ReadAllText(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
