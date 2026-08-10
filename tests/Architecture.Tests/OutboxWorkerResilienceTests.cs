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
    public void DurableOutboxHostLogsAndRetriesAfterDispatchFailure(
        string relativePath,
        string logMarker,
        string retryDelayMarker)
    {
        var repository = RepositoryModel.Load();
        var source = File.ReadAllText(Path.Combine(
            repository.Root.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        var dispatchOffset = source.IndexOf(
            "DispatchOnceAsync(stoppingToken)",
            StringComparison.Ordinal);
        var catchOffset = source.IndexOf(
            "catch (Exception exception)",
            dispatchOffset,
            StringComparison.Ordinal);
        var logOffset = source.IndexOf(logMarker, catchOffset, StringComparison.Ordinal);
        var delayOffset = source.IndexOf(retryDelayMarker, logOffset, StringComparison.Ordinal);

        Assert.True(
            dispatchOffset >= 0 && catchOffset > dispatchOffset &&
            logOffset > catchOffset && delayOffset > logOffset,
            $"Outbox host '{relativePath}' must log and delay before retrying a failed durable dispatch.");
        Assert.Contains(
            "catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("throw;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatcherRecordsTheExactFailureBeforeReturningControlToTheHostLoop()
    {
        var repository = RepositoryModel.Load();
        var source = File.ReadAllText(Path.Combine(
            repository.Root.FullName,
            "src/BuildingBlocks/Platform.Messaging/PostgresOutboxDispatcher.cs"
                .Replace('/', Path.DirectorySeparatorChar)));

        var publishOffset = source.IndexOf(
            "await _publisher.PublishAsync(message, cancellationToken);",
            StringComparison.Ordinal);
        var failureOffset = source.IndexOf(
            "await MarkFailedAsync(",
            publishOffset,
            StringComparison.Ordinal);
        var rethrowOffset = source.IndexOf("throw;", failureOffset, StringComparison.Ordinal);

        Assert.True(
            publishOffset >= 0 && failureOffset > publishOffset && rethrowOffset > failureOffset,
            "The dispatcher must persist the failed attempt before the execution host applies its retry delay.");
    }
}
