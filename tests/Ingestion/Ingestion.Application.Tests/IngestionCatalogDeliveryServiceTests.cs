using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Application;

namespace Ingestion.Application.Tests;

public sealed class IngestionCatalogDeliveryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 10, 30, 0, TimeSpan.Zero);
    private static readonly Guid DeliveryId = Guid.Parse("0198b100-0000-7000-8000-000000000001");
    private static readonly Guid BatchId = Guid.Parse("0198b100-0000-7000-8000-000000000002");
    private static readonly Guid LeaseToken = Guid.Parse("0198b100-0000-7000-8000-000000000003");
    private static readonly Guid ListingId = Guid.Parse("0198b100-0000-7000-8000-000000000004");
    private static readonly Guid ListingRevisionId = Guid.Parse("0198b100-0000-7000-8000-000000000005");

    [Fact]
    public async Task SuccessfulCatalogOutcomeConsumesExactLease()
    {
        var lease = CreateLease(attemptCount: 1);
        var store = new CapturingStore(lease);
        var client = new StubClient(CreateOutcome(lease));
        var service = CreateService(store, client, new FixedClassifier(retry: false));

        var processed = await service.ProcessAsync(
            "ingestion-catalog-delivery-worker",
            10,
            TimeSpan.FromMinutes(2),
            8,
            CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(1, client.CallCount);
        var result = Assert.IsType<IngestionCatalogDeliveryResult>(store.Result);
        Assert.Equal(DeliveryId, result.DeliveryId);
        Assert.Equal(LeaseToken, result.LeaseToken);
        Assert.Equal(ListingId, result.Outcome.ListingId);
        Assert.Null(store.RetryFailure);
        Assert.Null(store.TerminalFailure);
    }

    [Fact]
    public async Task TransientFailureSchedulesBoundedRetryWithExactLease()
    {
        var lease = CreateLease(attemptCount: 2);
        var store = new CapturingStore(lease);
        var client = new StubClient(new HttpRequestException("Catalog is unavailable."));
        var service = CreateService(store, client, new FixedClassifier(retry: true));

        var processed = await service.ProcessAsync(
            "ingestion-catalog-delivery-worker",
            10,
            TimeSpan.FromMinutes(2),
            8,
            CancellationToken.None);

        Assert.Equal(1, processed);
        var failure = Assert.IsType<IngestionCatalogDeliveryFailure>(store.RetryFailure);
        Assert.Equal(LeaseToken, failure.LeaseToken);
        Assert.Equal("INGESTION_CATALOG_TRANSIENT", failure.FailureCode);
        Assert.Equal(Now.AddSeconds(30), store.NextAttemptAtUtc);
        Assert.Null(store.Result);
        Assert.Null(store.TerminalFailure);
    }

    [Fact]
    public async Task AttemptLimitFailsDeliveryWithoutCallingCatalog()
    {
        var lease = CreateLease(attemptCount: 9);
        var store = new CapturingStore(lease);
        var client = new StubClient(CreateOutcome(lease));
        var service = CreateService(store, client, new FixedClassifier(retry: true));

        var processed = await service.ProcessAsync(
            "ingestion-catalog-delivery-worker",
            10,
            TimeSpan.FromMinutes(2),
            8,
            CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(0, client.CallCount);
        var failure = Assert.IsType<IngestionCatalogDeliveryFailure>(store.TerminalFailure);
        Assert.Equal("INGESTION_CATALOG_DELIVERY_ATTEMPT_LIMIT_EXCEEDED", failure.FailureCode);
        Assert.Equal(LeaseToken, failure.LeaseToken);
    }

    [Fact]
    public async Task StaleWorkerCannotOverwriteReplacementAttempt()
    {
        var lease = CreateLease(attemptCount: 1);
        var store = new CapturingStore(lease)
        {
            LoseLeaseOnOutcome = true,
        };
        var client = new StubClient(CreateOutcome(lease));
        var service = CreateService(store, client, new FixedClassifier(retry: false));

        var processed = await service.ProcessAsync(
            "ingestion-catalog-delivery-worker",
            10,
            TimeSpan.FromMinutes(2),
            8,
            CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(1, client.CallCount);
        Assert.Null(store.RetryFailure);
        Assert.Null(store.TerminalFailure);
    }

    private static ProcessIngestionCatalogDeliveriesService CreateService(
        IIngestionCatalogDeliveryStore store,
        IIngestionCatalogCommandClient client,
        IIngestionCatalogDeliveryFailureClassifier classifier) =>
        new(store, client, classifier, new FixedTimeProvider(Now));

    private static IngestionCatalogDeliveryLease CreateLease(int attemptCount)
    {
        var command = CreateCommand();
        return new IngestionCatalogDeliveryLease(
            DeliveryId,
            BatchId,
            "item-001",
            LeaseToken,
            Now.AddMinutes(2),
            command,
            command.CommandDigest,
            attemptCount);
    }

    private static CatalogIngestionUpsertDraftCommand CreateCommand()
    {
        var fields = new[]
        {
            new CatalogDraftFieldValueContract(
                "name",
                CatalogDraftValueKindContract.Text,
                "Example Provider",
                "en-GB",
                "collector",
                new string('a', 64),
                "public"),
        };
        var input = new CatalogIngestionCommandDigestInput(
            DeliveryId,
            BatchId,
            "item-001",
            "site",
            "catalog",
            Guid.Parse("0198b100-0000-7000-8000-000000000006"),
            "provider",
            "provider:example",
            fields,
            Now);
        return new CatalogIngestionUpsertDraftCommand(
            input.CommandId,
            input.IngestionBatchId,
            input.IngestionItemKey,
            CatalogIngestionCommandDigest.Compute(input),
            input.SiteKey,
            input.CatalogKey,
            input.ExpectedCatalogConfigurationRevisionId,
            input.EntityKind,
            input.SubjectNaturalKey,
            input.Fields,
            input.RequestedAtUtc,
            "ingestion-delivery-test");
    }

    private static CatalogIngestionCommandOutcome CreateOutcome(
        IngestionCatalogDeliveryLease lease) =>
        new(
            lease.DeliveryId,
            lease.BatchId,
            lease.ItemKey,
            CatalogIngestionOutcomeStateContract.DraftCreated,
            ListingId,
            ListingRevisionId,
            null,
            null,
            Now);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class StubClient : IIngestionCatalogCommandClient
    {
        private readonly CatalogIngestionCommandOutcome? _outcome;
        private readonly Exception? _exception;

        public StubClient(CatalogIngestionCommandOutcome outcome)
        {
            _outcome = outcome;
        }

        public StubClient(Exception exception)
        {
            _exception = exception;
        }

        public int CallCount { get; private set; }

        public Task<CatalogIngestionCommandOutcome> SendAsync(
            CatalogIngestionUpsertDraftCommand command,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return _exception is null
                ? Task.FromResult(_outcome!)
                : Task.FromException<CatalogIngestionCommandOutcome>(_exception);
        }
    }

    private sealed class FixedClassifier(bool retry) : IIngestionCatalogDeliveryFailureClassifier
    {
        public IngestionCatalogDeliveryFailureDecision Classify(
            Exception exception,
            int attempt,
            int maximumAttempts,
            DateTimeOffset failedAtUtc) =>
            retry
                ? new IngestionCatalogDeliveryFailureDecision(
                    true,
                    failedAtUtc.AddSeconds(30),
                    "INGESTION_CATALOG_TRANSIENT",
                    exception.Message)
                : new IngestionCatalogDeliveryFailureDecision(
                    false,
                    null,
                    "INGESTION_CATALOG_TERMINAL",
                    exception.Message);
    }

    private sealed class CapturingStore(params IngestionCatalogDeliveryLease[] leases)
        : IIngestionCatalogDeliveryStore
    {
        public IngestionCatalogDeliveryResult? Result { get; private set; }

        public IngestionCatalogDeliveryFailure? RetryFailure { get; private set; }

        public IngestionCatalogDeliveryFailure? TerminalFailure { get; private set; }

        public DateTimeOffset? NextAttemptAtUtc { get; private set; }

        public bool LoseLeaseOnOutcome { get; init; }

        public Task<IReadOnlyList<IngestionCatalogDeliveryLease>> LeaseAsync(
            string workerIdentity,
            int limit,
            DateTimeOffset leasedAtUtc,
            DateTimeOffset leaseExpiresAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IngestionCatalogDeliveryLease>>(leases);

        public Task<IngestionProcessingSnapshot> RecordOutcomeAsync(
            IngestionCatalogDeliveryResult result,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken)
        {
            if (LoseLeaseOnOutcome)
            {
                throw new IngestionCatalogDeliveryLeaseLostException(result.DeliveryId);
            }

            Result = result;
            return Task.FromResult<IngestionProcessingSnapshot>(null!);
        }

        public Task ScheduleRetryAsync(
            IngestionCatalogDeliveryFailure failure,
            DateTimeOffset nextAttemptAtUtc,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken)
        {
            RetryFailure = failure;
            NextAttemptAtUtc = nextAttemptAtUtc;
            return Task.CompletedTask;
        }

        public Task<IngestionProcessingSnapshot> FailAsync(
            IngestionCatalogDeliveryFailure failure,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken)
        {
            TerminalFailure = failure;
            return Task.FromResult<IngestionProcessingSnapshot>(null!);
        }
    }
}
