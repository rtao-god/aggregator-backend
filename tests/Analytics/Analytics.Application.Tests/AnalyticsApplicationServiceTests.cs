using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;

namespace Analytics.Application.Tests;

public sealed class AnalyticsApplicationServiceTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 4, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AcceptedInteractionIsPersistedAgainstKnownPublicRevision()
    {
        var eventId = Guid.Parse("0198a200-0000-7000-8000-000000000001");
        var eventStore = new InMemoryEventStore();
        var antiAbuse = new RecordingAntiAbuseVerifier();
        var service = CreateSubmitService(
            eventStore,
            new FixedPublicReadReferenceStore(PublicReadMembershipState.Known),
            antiAbuse,
            eventId);

        var response = await service.SubmitAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(eventId, response.EventId);
        Assert.Equal(InteractionAcceptanceStateContract.Accepted, response.AcceptanceState);
        Assert.Equal(TrafficQualityStateContract.Accepted, response.QualityState);
        Assert.Equal(1, antiAbuse.VerificationCount);
        Assert.Single(eventStore.Events);
    }

    [Fact]
    public async Task SameSemanticEventAndPayloadReturnsPriorResultWithoutReverification()
    {
        var eventStore = new InMemoryEventStore();
        var antiAbuse = new RecordingAntiAbuseVerifier();
        var service = CreateSubmitService(
            eventStore,
            new FixedPublicReadReferenceStore(PublicReadMembershipState.Known),
            antiAbuse,
            Guid.Parse("0198a200-0000-7000-8000-000000000002"));
        var request = CreateRequest();

        var first = await service.SubmitAsync(request, CancellationToken.None);
        var second = await service.SubmitAsync(
            request with { AntiAbuseToken = "rotated-transport-proof" },
            CancellationToken.None);

        Assert.Equal(first.EventId, second.EventId);
        Assert.Equal(InteractionAcceptanceStateContract.AlreadyApplied, second.AcceptanceState);
        Assert.Equal(1, antiAbuse.VerificationCount);
        Assert.Single(eventStore.Events);
    }

    [Fact]
    public async Task SameSemanticEventWithDifferentPayloadIsConflict()
    {
        var service = CreateSubmitService(
            new InMemoryEventStore(),
            new FixedPublicReadReferenceStore(PublicReadMembershipState.Known),
            new RecordingAntiAbuseVerifier(),
            Guid.Parse("0198a200-0000-7000-8000-000000000003"));
        var request = CreateRequest();
        _ = await service.SubmitAsync(request, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<AnalyticsCommandException>(() =>
            service.SubmitAsync(
                request with { PageContext = "listing_detail" },
                CancellationToken.None));

        Assert.Equal("ANALYTICS_EVENT_IDEMPOTENCY_CONFLICT", exception.Code);
        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task CampaignParameterOrderingDoesNotChangeSemanticPayloadDigest()
    {
        var eventStore = new InMemoryEventStore();
        var service = CreateSubmitService(
            eventStore,
            new FixedPublicReadReferenceStore(PublicReadMembershipState.Known),
            new RecordingAntiAbuseVerifier(),
            Guid.Parse("0198a200-0000-7000-8000-000000000004"));
        var firstParameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["utm_source"] = "newsletter",
            ["utm_campaign"] = "august",
        };
        var secondParameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["utm_campaign"] = "august",
            ["utm_source"] = "newsletter",
        };
        var request = CreateRequest() with { CampaignParameters = firstParameters };

        _ = await service.SubmitAsync(request, CancellationToken.None);
        var repeated = await service.SubmitAsync(
            request with { CampaignParameters = secondParameters },
            CancellationToken.None);

        Assert.Equal(InteractionAcceptanceStateContract.AlreadyApplied, repeated.AcceptanceState);
        Assert.Single(eventStore.Events);
    }

    [Fact]
    public async Task UnknownPublicReadRevisionFailsClosedBeforePersistence()
    {
        var eventStore = new InMemoryEventStore();
        var service = CreateSubmitService(
            eventStore,
            new FixedPublicReadReferenceStore(PublicReadMembershipState.UnknownRevision),
            new RecordingAntiAbuseVerifier(),
            Guid.Parse("0198a200-0000-7000-8000-000000000005"));

        var exception = await Assert.ThrowsAsync<AnalyticsCommandException>(() =>
            service.SubmitAsync(CreateRequest(), CancellationToken.None));

        Assert.Equal("ANALYTICS_PUBLIC_READ_REVISION_UNKNOWN", exception.Code);
        Assert.Equal(422, exception.StatusCode);
        Assert.Empty(eventStore.Events);
    }

    [Fact]
    public async Task CompleteAndUnavailableMetricsRemainDistinctInReadContract()
    {
        var listingId = Guid.Parse("0198a200-0000-7000-8000-000000000006");
        var metrics = new DailyListingMetrics[]
        {
            DailyListingMetrics.Complete(
                new DateOnly(2026, 8, 3),
                "berlin-recording-services",
                listingId,
                new string('a', 64),
                sourceReadRevisionCount: 1,
                InteractionCounts.Create(0, 0, 0, 0, 0, 0, 0, 0, 0)),
            DailyListingMetrics.Unavailable(
                new DateOnly(2026, 8, 4),
                "berlin-recording-services",
                listingId,
                new string('b', 64),
                sourceReadRevisionCount: 2,
                AggregateReadinessState.Partial,
                "late-events"),
        };
        var service = new ReadDailyListingMetricsService(
            new FixedMetricsStore(metrics),
            new AllowingMetricsAuthorizer());

        var response = await service.ReadAsync(
            Guid.Parse("0198a200-0000-7000-8000-000000000007"),
            "berlin-recording-services",
            listingId,
            new DailyMetricsRangeRequest(
                new DateOnly(2026, 8, 3),
                new DateOnly(2026, 8, 5)),
            CancellationToken.None);

        Assert.Equal(2, response.Count);
        Assert.Equal(AggregateReadinessStateContract.Complete, response[0].Readiness);
        Assert.NotNull(response[0].Counts);
        Assert.Equal(0, response[0].Counts!.OrganicImpressions);
        Assert.Equal(AggregateReadinessStateContract.Partial, response[1].Readiness);
        Assert.Null(response[1].Counts);
        Assert.Equal("late-events", response[1].UnavailableReason);
    }

    [Fact]
    public async Task MissingAggregateDateIsTypedUnavailableInsteadOfEmptyOrZero()
    {
        var listingId = Guid.Parse("0198a200-0000-7000-8000-000000000008");
        var service = new ReadDailyListingMetricsService(
            new FixedMetricsStore(
            [
                DailyListingMetrics.Complete(
                    new DateOnly(2026, 8, 3),
                    "berlin-recording-services",
                    listingId,
                    new string('c', 64),
                    sourceReadRevisionCount: 1,
                    InteractionCounts.Create(1, 0, 0, 0, 0, 0, 0, 0, 0)),
            ]),
            new AllowingMetricsAuthorizer());

        var exception = await Assert.ThrowsAsync<AnalyticsCommandException>(() =>
            service.ReadAsync(
                Guid.Parse("0198a200-0000-7000-8000-000000000009"),
                "berlin-recording-services",
                listingId,
                new DailyMetricsRangeRequest(
                    new DateOnly(2026, 8, 3),
                    new DateOnly(2026, 8, 5)),
                CancellationToken.None));

        Assert.Equal("ANALYTICS_AGGREGATE_COVERAGE_INCOMPLETE", exception.Code);
        Assert.Equal(503, exception.StatusCode);
    }

    private static SubmitInteractionEventService CreateSubmitService(
        InMemoryEventStore eventStore,
        IPublicReadReferenceStore references,
        RecordingAntiAbuseVerifier antiAbuse,
        Guid eventId) =>
        new(
            eventStore,
            references,
            antiAbuse,
            new FixedIdSource(eventId),
            new FixedTimeProvider(Timestamp));

    private static SubmitInteractionEventRequest CreateRequest() =>
        new(
            Guid.Parse("0198a200-0000-7000-8000-000000000010"),
            InteractionEventKindContract.ListingImpression,
            "berlin-recording-services",
            Guid.Parse("0198a200-0000-7000-8000-000000000011"),
            Guid.Parse("0198a200-0000-7000-8000-000000000012"),
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

    private sealed class FixedIdSource(Guid value) : IAnalyticsIdSource
    {
        public Guid CreateId() => value;
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
            Assert.False(string.IsNullOrWhiteSpace(antiAbuseToken));
            Assert.NotEqual(Guid.Empty, clientEventId);
            Assert.Equal(TimeSpan.Zero, occurredAtUtc.Offset);
            VerificationCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedPublicReadReferenceStore(PublicReadMembershipState state)
        : IPublicReadReferenceStore
    {
        public Task<PublicReadMembershipResult> ValidateMembershipAsync(
            Guid publicReadRevisionId,
            string catalogKey,
            Guid? listingId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PublicReadMembershipResult(
                state,
                state == PublicReadMembershipState.Known ? catalogKey : null,
                state == PublicReadMembershipState.Known ? listingId : null));
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
            _events.TryGetValue(semanticKey, out var existing);
            return Task.FromResult(existing);
        }

        public Task<InteractionEventRegistrationResult> RegisterAsync(
            InteractionEvent interactionEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(interactionEvent.SemanticKey, out var existing))
            {
                _events.Add(interactionEvent.SemanticKey, interactionEvent);
                return Task.FromResult(new InteractionEventRegistrationResult(
                    InteractionEventRegistrationState.Stored,
                    interactionEvent));
            }

            var state = string.Equals(
                existing.PayloadDigest,
                interactionEvent.PayloadDigest,
                StringComparison.Ordinal)
                ? InteractionEventRegistrationState.AlreadyApplied
                : InteractionEventRegistrationState.DigestConflict;
            return Task.FromResult(new InteractionEventRegistrationResult(state, existing));
        }
    }

    private sealed class FixedMetricsStore(IReadOnlyList<DailyListingMetrics> metrics)
        : IDailyListingMetricsStore
    {
        public Task<IReadOnlyList<DailyListingMetrics>> GetRangeAsync(
            string catalogKey,
            Guid listingId,
            DateOnly fromInclusive,
            DateOnly toExclusive,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(metrics);
        }
    }

    private sealed class AllowingMetricsAuthorizer : IListingMetricsAuthorizer
    {
        public Task AuthorizeAsync(
            Guid actorId,
            Guid listingId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotEqual(Guid.Empty, actorId);
            Assert.NotEqual(Guid.Empty, listingId);
            return Task.CompletedTask;
        }
    }
}
