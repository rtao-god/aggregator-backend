namespace Ingestion.Infrastructure.Tests;

public sealed class IngestionProducerRegistrationPersistenceTests
{
    [Fact]
    public void MigrationRejectsUnownedLegacyRowsAndCreatesImmutableLineage()
    {
        var migration = ReadRepositoryFile(
            "src/Ingestion/Ingestion.Migrations/Migrations/V008__producer_registration_owner.sql");

        Assert.Contains(
            "IF EXISTS (SELECT 1 FROM contracts.producer_registration)",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "producer_registration contains legacy rows without revision and command lineage",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE contracts.producer_registration_revision",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE operations.producer_registration_command",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "FOREIGN KEY (identity, aggregate_revision)",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "DEFERRABLE INITIALLY DEFERRED",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "trg_producer_registration_revision_immutable",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "trg_producer_registration_command_immutable",
            migration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StoreSerializesCommandAndProducerMutationsUnderSeparateLocks()
    {
        var core = ReadRepositoryFile(
            "src/Ingestion/Ingestion.Infrastructure/PostgresIngestionProducerRegistrationStore.Core.cs");
        var write = ReadRepositoryFile(
            "src/Ingestion/Ingestion.Infrastructure/PostgresIngestionProducerRegistrationStore.Write.cs");
        var validation = ReadRepositoryFile(
            "src/Ingestion/Ingestion.Infrastructure/PostgresIngestionProducerRegistrationStore.Validation.cs");

        Assert.Contains("IsolationLevel.Serializable", core, StringComparison.Ordinal);
        Assert.Contains("CommandLockSeed", core, StringComparison.Ordinal);
        Assert.Contains("ProducerLockSeed", core, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", core, StringComparison.Ordinal);
        Assert.Contains("ReadCommandAsync(", core, StringComparison.Ordinal);
        Assert.Contains("EnsureExpectedRevision(current, mutation);", core, StringComparison.Ordinal);
        Assert.Contains("InsertRevisionAsync(", core, StringComparison.Ordinal);
        Assert.Contains("UpsertCurrentAsync(", core, StringComparison.Ordinal);
        Assert.Contains("InsertCommandAsync(", core, StringComparison.Ordinal);

        Assert.Contains(
            "INSERT INTO contracts." + "producer_registration_revision",
            write,
            StringComparison.Ordinal);
        Assert.Contains(
            "INSERT INTO operations.producer_registration_command",
            write,
            StringComparison.Ordinal);
        Assert.Contains(
            "INGESTION_PRODUCER_IDEMPOTENCY_CONFLICT",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "INGESTION_PRODUCER_COMMAND_CORRUPT",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "IngestionProducerRegistrationService.ComputeContentDigest(",
            validation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyEfProducerModelIsAbsent()
    {
        var rows = ReadRepositoryFile(
            "src/Ingestion/Ingestion.Infrastructure/IngestionPersistenceRows.cs");
        var context = ReadRepositoryFile(
            "src/Ingestion/Ingestion.Infrastructure/IngestionDbContext.cs");
        var composition = ReadRepositoryFile(
            "src/Ingestion/Ingestion.Infrastructure/IngestionInfrastructureServiceCollectionExtensions.cs");

        Assert.DoesNotContain("IngestionProducerRow", rows, StringComparison.Ordinal);
        Assert.DoesNotContain("DbSet<IngestionProducerRow>", context, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureProducer(", context, StringComparison.Ordinal);
        Assert.Contains(
            "IIngestionProducerRegistry, IngestionProducerRegistry",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "IIngestionProducerRegistrationStore,",
            composition,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
