using Xunit;

namespace Architecture.Tests;

public sealed class AnalyticsAggregationReachabilityTests
{
    [Fact]
    public void WorkerExecutesTheLeaseBoundAggregationOwner()
    {
        var repository = RepositoryModel.Load();
        var program = Read(repository, "src/Analytics/Analytics.Worker/Program.cs");
        var service = Read(
            repository,
            "src/Analytics/Analytics.Application/RebuildDailyAnalyticsMetricsService.cs");
        var infrastructure = Read(
            repository,
            "src/Analytics/Analytics.Infrastructure/AnalyticsInfrastructureServiceCollectionExtensions.cs");

        Assert.Contains(
            "AddHostedService<AnalyticsAggregationWorker>()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetRequiredService<RebuildDailyAnalyticsMetricsService>()",
            program,
            StringComparison.Ordinal);
        Assert.Contains("result.RunId", program, StringComparison.Ordinal);
        Assert.Contains("result.SourceDigest", program, StringComparison.Ordinal);
        Assert.Contains(
            "IAnalyticsAggregationOperationStore operationStore",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "IAnalyticsAggregateWriter aggregateWriter",
            service,
            StringComparison.Ordinal);
        var beginOffset = service.IndexOf(
            "await operationStore.BeginAsync(",
            StringComparison.Ordinal);
        var writeOffset = service.IndexOf(
            "await aggregateWriter.RebuildAsync(",
            StringComparison.Ordinal);
        Assert.True(
            beginOffset >= 0 && writeOffset > beginOffset,
            "Aggregation lease must be persisted before materialization starts.");
        Assert.Contains(
            "IAnalyticsAggregationOperationStore,",
            infrastructure,
            StringComparison.Ordinal);
        Assert.Contains(
            "PostgresAnalyticsAggregationOperationStore",
            infrastructure,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateCompletionCommitsMetricsReadinessAndRunTogether()
    {
        var repository = RepositoryModel.Load();
        var writer = Read(
            repository,
            "src/Analytics/Analytics.Infrastructure/EfAnalyticsAggregateWriter.cs");
        var store = Read(
            repository,
            "src/Analytics/Analytics.Infrastructure/PostgresAnalyticsAggregationOperationStore.cs");
        var migration = Read(
            repository,
            "src/Analytics/Analytics.Migrations/Migrations/V006__aggregate_run_readiness.sql");

        Assert.Contains("IsolationLevel.Serializable", writer, StringComparison.Ordinal);
        Assert.Contains("EnsureActiveLease(runRow, lease)", writer, StringComparison.Ordinal);
        Assert.Contains(
            "dbContext.AggregateRunItems.Add(",
            writer,
            StringComparison.Ordinal);
        Assert.Contains(
            "dbContext.AggregateReadiness.Add(",
            writer,
            StringComparison.Ordinal);
        Assert.Contains(
            "runRow.State = (int)AnalyticsAggregateRunState.Complete",
            writer,
            StringComparison.Ordinal);
        var saveOffset = writer.LastIndexOf(
            "await dbContext.SaveChangesAsync(",
            StringComparison.Ordinal);
        var commitOffset = writer.LastIndexOf(
            "await transaction.CommitAsync(",
            StringComparison.Ordinal);
        Assert.True(
            saveOffset >= 0 && commitOffset > saveOffset,
            "Metrics, readiness, and terminal run state must commit in one transaction.");
        Assert.Contains(
            "ANALYTICS_PUBLIC_READ_REFERENCE_UNAVAILABLE",
            writer,
            StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", store, StringComparison.Ordinal);
        Assert.Contains(
            "ANALYTICS_AGGREGATION_LEASE_STALE",
            store,
            StringComparison.Ordinal);

        Assert.Contains(
            "CREATE TABLE aggregates.aggregate_run",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE aggregates.aggregate_run_item",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE aggregates.aggregate_readiness",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE UNIQUE INDEX ux_analytics_aggregate_run_rebuilding",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "OLD.state <> 1 OR NEW.state NOT IN (2, 3)",
            migration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyticsApiExposesBoundedBatchSummaryAndReadinessOwners()
    {
        var repository = RepositoryModel.Load();
        var controller = Read(
            repository,
            "src/Analytics/Analytics.Api/AnalyticsObservationsController.cs");
        var contracts = Read(
            repository,
            "src/Analytics/Analytics.Api/AnalyticsApiContracts.cs");
        var batchService = Read(
            repository,
            "src/Analytics/Analytics.Application/SubmitInteractionEventBatchService.cs");
        var summaryService = Read(
            repository,
            "src/Analytics/Analytics.Application/ReadListingMetricsSummaryService.cs");
        var statusService = Read(
            repository,
            "src/Analytics/Analytics.Application/ReadAnalyticsAggregationStatusService.cs");

        Assert.Contains(
            "[HttpPost(\"interaction-events/batch\", Name = AnalyticsOperationIds.SubmitInteractionEventBatch)]",
            controller,
            StringComparison.Ordinal);
        Assert.Contains(
            "RequestSizeLimit(AnalyticsRequestLimits.InteractionEventBatchMaximumBodyBytes)",
            controller,
            StringComparison.Ordinal);
        Assert.Contains(
            "[HttpGet(\"summary\", Name = AnalyticsOperationIds.ReadListingMetricsSummary)]",
            controller,
            StringComparison.Ordinal);
        Assert.Contains(
            "[HttpGet(\"aggregation-status\", Name = AnalyticsOperationIds.ReadAggregationStatus)]",
            controller,
            StringComparison.Ordinal);
        Assert.Contains(
            "AnalyticsAuthorizationPolicies.ViewAggregationStatus",
            controller,
            StringComparison.Ordinal);
        Assert.Contains(
            "SubmitAnalyticsInteractionEventBatch",
            contracts,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadAnalyticsListingMetricsSummary",
            contracts,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadAnalyticsAggregationStatus",
            contracts,
            StringComparison.Ordinal);
        Assert.Contains(
            "InteractionEventBatchItemStateContract.Rejected",
            batchService,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catch (Exception",
            batchService,
            StringComparison.Ordinal);
        Assert.Contains("Counts: null", summaryService, StringComparison.Ordinal);
        Assert.Contains(
            "AnalyticsAggregateRunState.Rebuilding",
            statusService,
            StringComparison.Ordinal);
        Assert.Contains(
            "AnalyticsAggregateRunState.Blocked",
            statusService,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AggregationPathUsesOnlyAnalyticsDatabaseAndLocalProjections()
    {
        var repository = RepositoryModel.Load();
        var compose = Read(repository, "compose.yaml");
        var workerStart = compose.IndexOf("  analytics-worker:", StringComparison.Ordinal);
        var workerEnd = compose.IndexOf("\n  promotion-api:", workerStart, StringComparison.Ordinal);
        Assert.True(
            workerStart >= 0 && workerEnd > workerStart,
            "Analytics worker service block was not found.");
        var workerBlock = compose[workerStart..workerEnd];
        var source = string.Join(
            '\n',
            Read(repository, "src/Analytics/Analytics.Application/RebuildDailyAnalyticsMetricsService.cs"),
            Read(repository, "src/Analytics/Analytics.Application/ReadAnalyticsAggregationStatusService.cs"),
            Read(repository, "src/Analytics/Analytics.Application/ReadListingMetricsSummaryService.cs"),
            Read(repository, "src/Analytics/Analytics.Infrastructure/EfAnalyticsAggregateWriter.cs"));

        Assert.Contains(
            "ConnectionStrings__Analytics:",
            workerBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "Analytics__Aggregation__LookbackDays:",
            workerBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConnectionStrings__Catalog",
            workerBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConnectionStrings__Query",
            workerBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Catalog.Api", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Query.Api", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings", source, StringComparison.Ordinal);
    }

    private static string Read(RepositoryModel repository, string relativePath) =>
        File.ReadAllText(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
