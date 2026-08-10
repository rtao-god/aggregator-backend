using Xunit;

namespace Architecture.Tests;

public sealed class OutboxWorkerResilienceTests
{
    public static TheoryData<string, string, string> DurableWorkers =>
        new()
        {
            {
                "src/Catalog/Catalog.Worker/CatalogOutboxWorker.cs",
                "CatalogOutboxWorkerLog.DispatchFailed(",
                "Task.Delay(options.EmptyDelay, stoppingToken)"
            },
            {
                "src/Query/Query.Worker/QueryOutboxWorker.cs",
                "QueryOutboxWorkerLog.DispatchFailed(",
                "Task.Delay(options.EmptyDelay, stoppingToken)"
            },
            {
                "src/Analytics/Analytics.Worker/AnalyticsOutboxWorker.cs",
                "AnalyticsOutboxWorkerLog.DispatchFailed(",
                "Task.Delay(options.EmptyDelay, stoppingToken)"
            },
            {
                "src/Promotion/Promotion.Worker/PromotionOwnerWorker.cs",
                "PromotionOwnerWorkerLog.OutboxDispatchFailed(",
                "Task.Delay(options.PollDelay, stoppingToken)"
            },
        };

    [Theory]
    [MemberData(nameof(DurableWorkers))]
    public void DurableOutboxHostLogsAndRetriesOnlyRecoverableDispatchFailures(
        string relativePath,
        string logMarker,
        string retryDelayMarker)
    {
        var repository = RepositoryModel.Load();
        var source = File.ReadAllText(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        var dispatchOffset = source.IndexOf(
            "DispatchOnceAsync(stoppingToken)",
            StringComparison.Ordinal);
        var catchOffset = source.IndexOf(
            "catch (Exception exception) when (",
            dispatchOffset,
            StringComparison.Ordinal);
        var policyOffset = source.IndexOf(
            "OutboxDispatchFailurePolicy.IsRecoverable(exception)",
            catchOffset,
            StringComparison.Ordinal);
        var logOffset = source.IndexOf(logMarker, policyOffset, StringComparison.Ordinal);
        var delayOffset = source.IndexOf(retryDelayMarker, logOffset, StringComparison.Ordinal);

        Assert.True(
            dispatchOffset >= 0 && catchOffset > dispatchOffset &&
            policyOffset > catchOffset && logOffset > policyOffset && delayOffset > logOffset,
            $"Outbox host '{relativePath}' must classify, log, and delay before retrying a recoverable durable dispatch failure.");
        Assert.Contains(
            "catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("throw;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatcherRecordsPublishFailureBeforeReturningTypedControlToTheHostLoop()
    {
        var repository = RepositoryModel.Load();
        var source = File.ReadAllText(Path.Combine(
            repository.Root,
            "src/BuildingBlocks/Platform.Messaging/PostgresOutboxDispatcher.cs"
                .Replace('/', Path.DirectorySeparatorChar)));

        var publishOffset = source.IndexOf(
            "await _publisher.PublishAsync(message, cancellationToken);",
            StringComparison.Ordinal);
        var failureOffset = source.IndexOf(
            "var deadLettered = await MarkFailedAsync(",
            publishOffset,
            StringComparison.Ordinal);
        var typedFailureOffset = source.IndexOf(
            "throw new OutboxDispatchAttemptException(",
            failureOffset,
            StringComparison.Ordinal);

        Assert.True(
            publishOffset >= 0 && failureOffset > publishOffset &&
            typedFailureOffset > failureOffset,
            "The dispatcher must persist the failed attempt before returning a typed recoverable failure to the execution host.");
    }
}
