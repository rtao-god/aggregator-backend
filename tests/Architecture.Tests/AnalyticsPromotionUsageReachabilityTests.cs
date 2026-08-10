using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

public sealed class AnalyticsPromotionUsageReachabilityTests
{
    [Fact]
    public void AnalyticsOwnsTheUsageContractAndPromotionReferencesOnlyItsContractsProject()
    {
        var repository = RepositoryModel.Load();
        var analyticsApplicationReferences = ReadProjectReferences(
            repository,
            "src/Analytics/Analytics.Application/Analytics.Application.csproj");
        var promotionWorkerReferences = ReadProjectReferences(
            repository,
            "src/Promotion/Promotion.Worker/Promotion.Worker.csproj");

        Assert.Contains(
            "../Analytics.Contracts/Analytics.Contracts.csproj",
            analyticsApplicationReferences);
        Assert.Contains(
            "../../Analytics/Analytics.Contracts/Analytics.Contracts.csproj",
            promotionWorkerReferences);
        Assert.DoesNotContain(
            promotionWorkerReferences,
            reference =>
                reference.Contains("Analytics.Application", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Analytics.Domain", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Analytics.Infrastructure", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Analytics.Api", StringComparison.OrdinalIgnoreCase));

        var producerContract = Read(
            repository,
            "src/Analytics/Analytics.Contracts/PromotionUsageIntegrationContracts.cs");
        Assert.Contains(
            "analytics.promotion-usage-window.closed",
            producerContract,
            StringComparison.Ordinal);
        Assert.Contains(
            "analytics.promotion-usage-window-closed@1",
            producerContract,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Promotion.Domain", producerContract, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyticsCommitsUsageRevisionOutboxAndAggregationRunInOneTransaction()
    {
        var repository = RepositoryModel.Load();
        var writer = Read(
            repository,
            "src/Analytics/Analytics.Infrastructure/EfAnalyticsAggregateWriter.cs");
        var materializer = Read(
            repository,
            "src/Analytics/Analytics.Infrastructure/AnalyticsPromotionUsageMaterializer.cs");
        var migration = Read(
            repository,
            "src/Analytics/Analytics.Migrations/Migrations/V007__promotion_usage_outbox.sql");
        var program = Read(
            repository,
            "src/Analytics/Analytics.Worker/Program.cs");

        var usageOffset = writer.IndexOf(
            "await promotionUsageMaterializer.MaterializeAsync(",
            StringComparison.Ordinal);
        var completeOffset = writer.IndexOf(
            "runRow.State = (int)AnalyticsAggregateRunState.Complete;",
            StringComparison.Ordinal);
        var saveOffset = writer.IndexOf(
            "await dbContext.SaveChangesAsync(cancellationToken);",
            StringComparison.Ordinal);
        var commitOffset = writer.IndexOf(
            "await transaction.CommitAsync(cancellationToken);",
            StringComparison.Ordinal);
        Assert.True(
            usageOffset >= 0 && completeOffset > usageOffset &&
            saveOffset > completeOffset && commitOffset > saveOffset,
            "Promotion usage, aggregate completion, EF persistence, and commit must share one ordered transaction.");

        Assert.Contains(
            "INSERT INTO messaging.outbox_message",
            materializer,
            StringComparison.Ordinal);
        Assert.Contains(
            "INSERT INTO aggregates.promotion_usage_window_revision",
            materializer,
            StringComparison.Ordinal);
        Assert.Contains(
            "PromotionUsageWindowDeriver.CreateZeroCorrection",
            materializer,
            StringComparison.Ordinal);
        Assert.Contains(
            "PromotionUsageOutboxMessageFactory.Create",
            materializer,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE messaging.outbox_message",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE aggregates.promotion_usage_window_revision",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "trg_analytics_promotion_usage_current_has_revision",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddAnalyticsOutboxWorker(outboxOptions)",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Promotion.Infrastructure", materializer, StringComparison.Ordinal);
        Assert.DoesNotContain("promotion_db", materializer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PromotionWorkerConsumesAndCommitsBeforeAcknowledgement()
    {
        var repository = RepositoryModel.Load();
        var program = Read(
            repository,
            "src/Promotion/Promotion.Worker/Program.cs");
        var worker = Read(
            repository,
            "src/Promotion/Promotion.Worker/PromotionUsageProjectionWorker.cs");
        var store = Read(
            repository,
            "src/Promotion/Promotion.Infrastructure/PostgresPromotionUsageProjectionStore.cs");
        var applicationRegistration = Read(
            repository,
            "src/Promotion/Promotion.Application/PromotionUsageProjectionApplicationExtensions.cs");
        var apiProgram = Read(
            repository,
            "src/Promotion/Promotion.Api/Program.cs");

        Assert.Contains(
            "AddPromotionUsageProjectionApplication()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddPromotionUsageProjectionInfrastructure()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddHostedService<PromotionUsageProjectionWorker>()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyAnalyticsPromotionUsageWindowService",
            applicationRegistration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddPromotionUsageProjectionApplication()",
            apiProgram,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddPromotionUsageProjectionInfrastructure()",
            apiProgram,
            StringComparison.Ordinal);

        var applyOffset = worker.IndexOf(
            "var result = await service.ApplyAsync(",
            StringComparison.Ordinal);
        var ackOffset = worker.IndexOf(
            "await channel.BasicAckAsync(",
            StringComparison.Ordinal);
        Assert.True(
            applyOffset >= 0 && ackOffset > applyOffset,
            "Promotion usage message must be acknowledged only after the application/store path completes.");
        Assert.Contains(
            "AnalyticsPromotionUsageIntegrationContracts.ContractIdentity",
            worker,
            StringComparison.Ordinal);
        Assert.Contains("payload-digest", worker, StringComparison.Ordinal);
        Assert.Contains("causation-id", worker, StringComparison.Ordinal);
        Assert.Contains("x-queue-type", worker, StringComparison.Ordinal);
        Assert.Contains("x-delivery-limit", worker, StringComparison.Ordinal);
        Assert.Contains("BasicNackAsync", worker, StringComparison.Ordinal);
        Assert.Contains("IsolationLevel.Serializable", store, StringComparison.Ordinal);
        Assert.Contains("PROMOTION_USAGE_REVISION_STALE", store, StringComparison.Ordinal);
        Assert.Contains("PROMOTION_USAGE_REVISION_GAP", store, StringComparison.Ordinal);
        Assert.Contains(
            "promotion_usage_window_revision",
            store,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("analytics_db", store, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkerCredentialsRemainInsideTheirDatabaseOwners()
    {
        var repository = RepositoryModel.Load();
        var compose = Read(repository, "compose.yaml");
        var analyticsWorker = ExtractService(compose, "analytics-worker", "promotion-api");
        var promotionWorker = ExtractService(compose, "promotion-worker", "reverse-proxy");

        Assert.Contains(
            "ConnectionStrings__Analytics",
            analyticsWorker,
            StringComparison.Ordinal);
        Assert.Contains(
            "Analytics__PublicReadProjection__BrokerUri",
            analyticsWorker,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConnectionStrings__Promotion",
            analyticsWorker,
            StringComparison.Ordinal);
        Assert.Contains(
            "ConnectionStrings__Promotion",
            promotionWorker,
            StringComparison.Ordinal);
        Assert.Contains("Messaging__BrokerUri", promotionWorker, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConnectionStrings__Analytics",
            promotionWorker,
            StringComparison.Ordinal);
    }

    private static string ExtractService(
        string compose,
        string service,
        string nextService)
    {
        var start = compose.IndexOf($"  {service}:", StringComparison.Ordinal);
        var end = compose.IndexOf($"\n  {nextService}:", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Compose service '{service}' was not found.");
        return compose[start..end];
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
