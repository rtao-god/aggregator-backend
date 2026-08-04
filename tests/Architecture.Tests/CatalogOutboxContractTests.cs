namespace Architecture.Tests;

public sealed class CatalogOutboxContractTests
{
    private static readonly string[] RequiredSqlColumns =
    [
        "message_id",
        "routing_key",
        "contract_identity",
        "payload_json",
        "payload_digest",
        "occurred_at_utc",
        "correlation_id",
        "causation_id",
        "lease_owner",
        "lease_until_utc",
        "delivery_attempts",
        "dispatched_at_utc",
        "last_error",
        "dead_lettered_at_utc",
        "dead_letter_reason",
    ];

    [Fact]
    public void CatalogMigrationExposesTheGenericDispatcherContract()
    {
        var migration = ReadRepositoryFile(
            "src",
            "Catalog",
            "Catalog.Migrations",
            "Migrations",
            "V002__catalog_durable_outbox.sql");

        foreach (var column in RequiredSqlColumns)
        {
            Assert.Contains(column, migration, StringComparison.Ordinal);
        }

        Assert.Contains("delivery_attempts >= 0", migration, StringComparison.Ordinal);
        Assert.Contains("dead_lettered_at_utc IS NULL", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogPersistenceDoesNotUseTheLegacyOutboxShape()
    {
        var rows = ReadRepositoryFile(
            "src",
            "Catalog",
            "Catalog.Infrastructure",
            "CatalogRows.cs");
        var producerPort = ReadRepositoryFile(
            "src",
            "Catalog",
            "Catalog.Application",
            "CatalogApplicationPorts.cs");

        Assert.Contains("ContractIdentity", producerPort, StringComparison.Ordinal);
        Assert.Contains("PayloadDigest", producerPort, StringComparison.Ordinal);
        Assert.Contains("CorrelationId", producerPort, StringComparison.Ordinal);
        Assert.Contains("MessageId", rows, StringComparison.Ordinal);
        Assert.Contains("DeadLetteredAtUtc", rows, StringComparison.Ordinal);
        Assert.DoesNotContain("EventRevision", rows, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishedAtUtc", rows, StringComparison.Ordinal);
        Assert.DoesNotContain("AttemptCount", rows, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine([root, .. segments]));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located from the test output directory.");
    }
}
