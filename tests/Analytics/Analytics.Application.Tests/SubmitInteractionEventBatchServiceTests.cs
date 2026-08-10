using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;

namespace Analytics.Application.Tests;

public sealed class SubmitInteractionEventBatchServiceTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IndependentItemFailureRemainsExplicitBesideAcceptedItem()
    {
        var eventStore = new InMemoryEventStore();
        var antiAbuse = new RecordingAntiAbuseVerifier();
        var single = CreateSubmitService(
            eventStore,
            new ListingAwarePublicReadReferenceStore(),
            antiAbuse);
        var service = new SubmitInteractionEventBatchService(single);
        var accepted = CreateRequest(
            Guid.Parse("01990100-0000-7000-8000-000000000001"),
            Guid.Parse("01990100-0000-7000-8000-000000000011"));
        var rejected = CreateRequest(
            Guid.Parse("01990100-0000-7000-8000-000000000002"),
            Guid.Parse("01990100-0000-7000-8000-000000000099"));

        var result = await service.SubmitAsync(
            new SubmitInteractionEventBatchRequest([accepted, rejected]),
            CancellationToken.None);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.AlreadyAppliedCount);
        Assert.Equal(1, result.RejectedCount);
        Assert.Collection(
            result.Items,
            item =>
            {
                Assert.Equal(0, item.Index);
                Assert.Equal(InteractionEventBatchItemStateContract.Accepted, item.State);
                Assert.NotNull(item.Event);
                Assert.Null(item.Failure);
            },
            item =>
            {
                Assert.Equal(1, item.Index);
                Assert.Equal(InteractionEventBatchItemStateContract.Rejected, item.State);
                Assert.Null(item.Event);
                Assert.Equal("ANALYTICS_PUBLIC_LISTING_UNKNOWN", item.Failure?.Code);
                Assert.Equal(422, item.Failure?.StatusCode);
            });
        Assert.Single(eventStore.Events);
        Assert.Equal(2, antiAbuse.VerificationCount);
    }

    [Fact]
    public async Task DuplicateSemanticIdentityBlocksEntireBatchBeforeSideEffects()
    {
        var eventStore = new InMemoryEventStore();
        var antiAbuse = new RecordingAntiAbuseVerifier();
        var service = new SubmitInteractionEventBatchService(CreateSubmitService(
            eventStore,
            new ListingAwarePublicReadReferenceStore(),
            antiAbuse));
        var first = CreateRequest(
            Guid.Parse("01990100-0000-7000-8000-000000000003"),
            Guid.Parse("01990100-0000-7000-8000-000000000011"));
        var duplicate = first with { PageContext = "listing_detail" };

        var exception = await Assert.ThrowsAsync<AnalyticsCommandException>(() =>
            service.SubmitAsync(
                new SubmitInteractionEventBatchRequest([first, duplicate]),
                CancellationToken.None));

        Assert.Equal(
            "ANALYTICS_INTERACTION_BATCH_SEMANTIC_IDENTITY_DUPLICATE",
            exception.Code);
        Assert.Equal(409, exception.StatusCode);
        Assert.Empty(eventStore.Events);
        Assert.Equal(0, antiAbuse.VerificationCount);
    }

    [Fact]
    public async Task OversizedBatchIsRejectedBeforeItemProcessing()
    {
        var eventStore = new InMemoryEventStore();
        var antiAbuse = new RecordingAntiAbuseVerifier();
        var service = new SubmitInteractionEventBatchService(CreateSubmitService(
            eventStore,
            new ListingAwarePublicReadReferenceStore(),
            antiAbuse));
        var events = Enumerable.Range(0, SubmitInteractionEventBatchService.MaximumEventCount + 1)
            .Select(index => CreateRequest(
                CreateDeterministicGuid(index + 100),
                Guid.Parse("01990100-0000-7000-8000-000000000011")))
            .ToArray();

        var exception = await Assert.ThrowsAsync<AnalyticsCommandException>(() =>
            service.SubmitAsync(
                new SubmitInteractionEventBatchRequest(events),
                CancellationToken.None));

        Assert.Equal("ANALYTICS_INTERACTION_BATCH_COUNT_INVALID", exception.Code);
        Assert.Equal(400, exception.StatusCode);
        Assert.Empty(eventStore.Events);
        Assert.Equal(0, antiAbuse.VerificationCount);
    }

    private static SubmitInteractionEventService CreateSubmitService(
        InMemoryEventStore eventStore,
        IPublicReadReferenceStore references,
        RecordingAntiAbuseVerifier antiAbuse) =>
        new(
            eventStore,
            references,
            antiAbuse,
            new IncrementingIdSource(),
            new FixedTimeProvider(Timestamp));

    private static SubmitInteractionEventRequest CreateRequest(
        Guid clientEventId,
        Guid listingId) =>
        new(
            clientEventId,
            InteractionEventKindContract.ListingImpression,
            "berlin-recording-services",
            listingId,
            Guid.Parse("01990100-0000-7000-8000-000000000020"),
            Timestamp,
            "catalog_results",
            new PlacementContextContract(
                PlacementExposureKindContract.Organic,
                PlacementId: null,
                ScopeKey: "recording-studio"),
            ReferrerClassContract.Internal,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ConsentModeContract.AnalyticsAllowed,
            "valid-transport-proof");

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private static Guid CreateDeterministicGuid(int suffix) =>
        Guid.Parse($"01990100-0000-7000-8000-{suffix:000000000000}");

    private sealed class IncrementingIdSource : IAnalyticsIdSource
    {
        private int _nextSuffix = 500;

        public Guid CreateId() => CreateDeterministicGuid(_nextSuffix++);
    }

    private sealed class RecordingAntiAbuseVerifier : IAntiAbuseVerifier
    {
        public int VerificationCount { get; private set; }

        public Task VerifyAsync(
            string antiAbuseToken,
            Guid clientEventId,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerificationCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ListingAwarePublicReadReferenceStore : IPublicReadReferenceStore
    {
        private static readonly Guid KnownListingId =
            Guid.Parse("01990100-0000-7000-8000-000000000011");

        public Task<PublicReadMembershipResult> ValidateInteractionAsync(
            Guid publicReadRevisionId,
            string catalogKey,
            Guid? listingId,
            PlacementContext placementContext,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(listingId == KnownListingId
                ? new PublicReadMembershipResult(
                    PublicReadMembershipState.Known,
                    catalogKey,
                    listingId)
                : new PublicReadMembershipResult(
                    PublicReadMembershipState.ListingNotPublic,
                    catalogKey,
                    listingId));
        }
    }

    private sealed class InMemoryEventStore : IAnalyticsEventStore
    {
        private readonly Dictionary<InteractionEventSemanticKey, InteractionEvent> _events = [];

        public IReadOnlyCollection<InteractionEvent> Events => _events.Values;

        public Task<InteractionEvent?> GetAsync(
            InteractionEventSemanticKey semanticKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.TryGetValue(semanticKey, out var value);
            return Task.FromResult(value);
        }

        public Task<InteractionEventRegistrationResult> RegisterAsync(
            InteractionEvent interactionEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_events.TryGetValue(interactionEvent.SemanticKey, out var existing))
            {
                return Task.FromResult(new InteractionEventRegistrationResult(
                    string.Equals(
                        existing.PayloadDigest,
                        interactionEvent.PayloadDigest,
                        StringComparison.Ordinal)
                        ? InteractionEventRegistrationState.AlreadyApplied
                        : InteractionEventRegistrationState.DigestConflict,
                    existing));
            }

            _events.Add(interactionEvent.SemanticKey, interactionEvent);
            return Task.FromResult(new InteractionEventRegistrationResult(
                InteractionEventRegistrationState.Stored,
                interactionEvent));
        }
    }
}
