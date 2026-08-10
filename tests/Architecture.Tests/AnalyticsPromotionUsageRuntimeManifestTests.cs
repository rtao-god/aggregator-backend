using System.Text.Json;
using Xunit;

namespace Architecture.Tests;

public sealed class AnalyticsPromotionUsageRuntimeManifestTests
{
    private const string ManifestPath =
        "contracts/analytics-promotion-usage-runtime-contract.json";

    [Fact]
    public void RuntimeManifestReferencesExistingOwnerArtifacts()
    {
        var root = FindRepositoryRoot();
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, ManifestPath)));
        var document = manifest.RootElement;

        Assert.Equal(
            "analytics-promotion-closed-usage",
            document.GetProperty("contract").GetString());
        Assert.Equal(
            "analytics.promotion-usage-window.closed",
            document.GetProperty("producer").GetProperty("routingKey").GetString());
        Assert.Equal(
            "analytics.promotion-usage-window-closed@1",
            document.GetProperty("producer").GetProperty("contractIdentity").GetString());

        AssertReferencedFilesExist(root, document.GetProperty("producer"));
        AssertReferencedFilesExist(root, document.GetProperty("consumer"));
        AssertReferencedFilesExist(root, document.GetProperty("operationalProof"));

        var semantics = document
            .GetProperty("requiredSemantics")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("explicit-observed-zero-correction", semantics);
        Assert.Contains("contiguous-aggregate-revisions", semantics);
        Assert.Contains("analytics-transactional-outbox", semantics);
        Assert.Contains("promotion-transactional-inbox", semantics);
        Assert.Contains("ack-after-persistence-commit", semantics);
        Assert.Contains("no-cross-database-access", semantics);
        Assert.Contains("no-synchronous-cross-context-call", semantics);
    }

    [Fact]
    public void ProducerAndConsumerUseTheProducerOwnedWireIdentity()
    {
        var root = FindRepositoryRoot();
        var contract = Read(
            root,
            "src/Analytics/Analytics.Contracts/PromotionUsageIntegrationContracts.cs");
        var consumerOptions = Read(
            root,
            "src/Promotion/Promotion.Worker/PromotionUsageProjectionWorkerOptions.cs");
        var workerProject = Read(
            root,
            "src/Promotion/Promotion.Worker/Promotion.Worker.csproj");

        Assert.Contains(
            "analytics.promotion-usage-window.closed",
            contract,
            StringComparison.Ordinal);
        Assert.Contains(
            "analytics.promotion-usage-window-closed@1",
            contract,
            StringComparison.Ordinal);
        Assert.Contains(
            "AnalyticsPromotionUsageIntegrationContracts.RoutingKey",
            consumerOptions,
            StringComparison.Ordinal);
        Assert.Contains(
            "AnalyticsPromotionUsageIntegrationContracts.ContractIdentity",
            consumerOptions,
            StringComparison.Ordinal);
        Assert.Contains(
            "Analytics.Contracts.csproj",
            workerProject,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UsagePersistenceIsRevisionedAndReplaySafe()
    {
        var root = FindRepositoryRoot();
        var analyticsMigration = Read(
            root,
            "src/Analytics/Analytics.Migrations/Migrations/V007__promotion_usage_outbox.sql");
        var promotionMigration = Read(
            root,
            "src/Promotion/Promotion.Migrations/Migrations/V006__analytics_promotion_usage_revisions.sql");
        var promotionStore = Read(
            root,
            "src/Promotion/Promotion.Infrastructure/PostgresPromotionUsageProjectionStore.cs");

        Assert.Contains("messaging.outbox_message", analyticsMigration, StringComparison.Ordinal);
        Assert.Contains("promotion_usage_window_revision", analyticsMigration, StringComparison.Ordinal);
        Assert.Contains("aggregate_revision", analyticsMigration, StringComparison.Ordinal);
        Assert.Contains("promotion_usage_window_revision", promotionMigration, StringComparison.Ordinal);
        Assert.Contains("current_aggregate_revision", promotionMigration, StringComparison.Ordinal);
        Assert.Contains("IsolationLevel.Serializable", promotionStore, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", promotionStore, StringComparison.Ordinal);
        Assert.Contains("PromotionUsageProjectionDisposition.Duplicate", promotionStore, StringComparison.Ordinal);
        Assert.Contains("REVISION_GAP", promotionStore, StringComparison.Ordinal);
        Assert.Contains("REVISION_STALE", promotionStore, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionAcknowledgementFollowsApplicationAndPersistence()
    {
        var root = FindRepositoryRoot();
        var consumer = Read(
            root,
            "src/Promotion/Promotion.Worker/PromotionUsageProjectionWorker.cs");
        var applyIndex = consumer.IndexOf("ApplyAsync(", StringComparison.Ordinal);
        var acknowledgeIndex = consumer.IndexOf("BasicAckAsync", StringComparison.Ordinal);

        Assert.True(applyIndex >= 0, "Promotion usage application call was not found.");
        Assert.True(acknowledgeIndex > applyIndex, "RabbitMQ ACK must follow the application/persistence call.");
    }

    [Fact]
    public void WorkersHaveNoForeignDatabaseOrSynchronousCrossContextPath()
    {
        var root = FindRepositoryRoot();
        var compose = Read(root, "compose.yaml");
        var analyticsWorker = ReadComposeServiceBlock(compose, "analytics-worker");
        var promotionWorker = ReadComposeServiceBlock(compose, "promotion-worker");
        var producerMaterializer = Read(
            root,
            "src/Analytics/Analytics.Infrastructure/AnalyticsPromotionUsageMaterializer.cs");
        var consumer = Read(
            root,
            "src/Promotion/Promotion.Worker/PromotionUsageProjectionWorker.cs");

        Assert.DoesNotContain("promotion_db", analyticsWorker, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog_db", analyticsWorker, StringComparison.Ordinal);
        Assert.DoesNotContain("analytics_db", promotionWorker, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog_db", promotionWorker, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", producerMaterializer, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", consumer, StringComparison.Ordinal);
    }

    private static void AssertReferencedFilesExist(string root, JsonElement owner)
    {
        foreach (var property in owner.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = property.Value.GetString();
            if (value is null ||
                (!property.Name.EndsWith("Path", StringComparison.Ordinal) &&
                 property.Name is not ("decision" or "runbook" or "module")))
            {
                continue;
            }

            Assert.True(
                File.Exists(Path.Combine(root, value)),
                $"Runtime contract artifact '{value}' does not exist.");
        }
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(root, relativePath));

    private static string ReadComposeServiceBlock(string compose, string serviceName)
    {
        var marker = $"  {serviceName}:";
        var start = compose.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Compose service '{serviceName}' was not found.");

        var nextService = compose.IndexOf("\n  ", start + marker.Length, StringComparison.Ordinal);
        while (nextService >= 0 &&
               nextService + 3 < compose.Length &&
               char.IsWhiteSpace(compose[nextService + 3]))
        {
            nextService = compose.IndexOf("\n  ", nextService + 3, StringComparison.Ordinal);
        }

        return nextService < 0
            ? compose[start..]
            : compose[start..nextService];
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

        throw new InvalidOperationException("Repository root could not be located.");
    }
}
