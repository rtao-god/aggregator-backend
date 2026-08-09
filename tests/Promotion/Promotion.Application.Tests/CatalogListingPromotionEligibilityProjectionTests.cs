using Aggregator.Catalog.Contracts;
using Aggregator.Promotion.Application;
using Aggregator.Promotion.Domain;

namespace Promotion.Application.Tests;

public sealed class CatalogListingPromotionEligibilityProjectionTests
{
    private static readonly DateTimeOffset OccurredAtUtc =
        new(2026, 8, 9, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReceivedAtUtc = OccurredAtUtc.AddSeconds(1);
    private static readonly Guid EventId =
        Guid.Parse("0198ff00-0000-7000-8000-000000000001");
    private static readonly Guid ListingId =
        Guid.Parse("0198ff00-0000-7000-8000-000000000002");
    private static readonly Guid RevisionId =
        Guid.Parse("0198ff00-0000-7000-8000-000000000003");
    private static readonly Guid SystemActorId =
        Guid.Parse("0198ff00-0000-7000-8000-000000000004");
    private const string PayloadDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ExactCatalogEventBecomesOneAtomicProjectionChange()
    {
        var store = new CapturingStore();
        var reconciler = new CapturingReconciler();
        var idSource = new FixedIdSource(
            Guid.Parse("0198ff00-0000-7000-8000-000000000005"));
        var service = new ApplyCatalogListingPromotionEligibilityService(
            store,
            reconciler,
            idSource,
            new FixedClock(ReceivedAtUtc));
        var integrationEvent = CreateEvent();

        var result = await service.ApplyAsync(
            new PromotionEligibilityProjectionMessage(
                EventId,
                CatalogIntegrationEventContracts.ListingPromotionEligibilityChanged,
                PayloadDigest,
                "correlation-eligibility-1",
                CausationId: null,
                integrationEvent),
            PromotionActor.Create(SystemActorId),
            CancellationToken.None);

        Assert.Equal(PromotionEligibilityProjectionApplyResult.Applied, result);
        var change = Assert.IsType<PromotionEligibilityProjectionChange>(store.Change);
        Assert.Equal(EventId, change.MessageId);
        Assert.Equal(RevisionId, change.PublishedListingRevisionId);
        Assert.Equal("berlin-recording-services", change.Eligibility.CatalogKey);
        Assert.Equal(ListingId, change.Eligibility.ListingId);
        Assert.True(change.Eligibility.IsPublished);
        Assert.False(change.Eligibility.IsArchived);
        Assert.True(change.Eligibility.HasVerifiedContact);
        Assert.Contains(
            CatalogPromotionContactCapabilities.Website,
            change.Eligibility.ContactCapabilities);
        Assert.Contains("recording-studio", change.Eligibility.CategoryKeys);
        Assert.Equal("mitte", change.Eligibility.DistrictKey);
        Assert.Equal(1, change.Eligibility.SourceRevision);
        Assert.Equal(OccurredAtUtc, change.Eligibility.ChangedAtUtc);
        Assert.Matches("^[0-9a-f]{64}$", change.ProjectionDigest);
        Assert.Equal(ReceivedAtUtc, store.ReceivedAtUtc);
        Assert.Equal(change.Eligibility, reconciler.Eligibility);
        Assert.Equal(SystemActorId, reconciler.CommandContext?.Actor.Id);
        Assert.Equal("correlation-eligibility-1", reconciler.CommandContext?.CorrelationId);
        Assert.Equal(EventId, reconciler.CommandContext?.CausationId);
        Assert.Equal(ReceivedAtUtc, reconciler.ChangedAtUtc);
        Assert.Same(idSource, reconciler.IdSource);
    }

    [Fact]
    public async Task ReplayedProjectionStillReconcilesPlacements()
    {
        var store = new CapturingStore(PromotionEligibilityProjectionApplyResult.Replayed);
        var reconciler = new CapturingReconciler();
        var service = new ApplyCatalogListingPromotionEligibilityService(
            store,
            reconciler,
            new FixedIdSource(
                Guid.Parse("0198ff00-0000-7000-8000-000000000006")),
            new FixedClock(ReceivedAtUtc));

        var result = await service.ApplyAsync(
            CreateMessage(CreateEvent()),
            PromotionActor.Create(SystemActorId),
            CancellationToken.None);

        Assert.Equal(PromotionEligibilityProjectionApplyResult.Replayed, result);
        Assert.Equal(1, reconciler.CallCount);
        Assert.Equal(EventId, reconciler.CommandContext?.CausationId);
    }

    [Fact]
    public async Task UnpublishedEventCannotRetainPublicEligibilityFacts()
    {
        var service = new ApplyCatalogListingPromotionEligibilityService(
            new UnexpectedStore(),
            new UnexpectedReconciler(),
            new FixedIdSource(
                Guid.Parse("0198ff00-0000-7000-8000-000000000090")),
            new FixedClock(ReceivedAtUtc));
        var invalidEvent = CreateEvent() with
        {
            PublishedListingRevisionId = null,
            IsPublished = false,
        };

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() =>
            service.ApplyAsync(
                CreateMessage(invalidEvent),
                PromotionActor.Create(SystemActorId),
                CancellationToken.None));

        Assert.Equal(
            "PROMOTION_ELIGIBILITY_UNPUBLISHED_FACTS_PRESENT",
            exception.Code);
        Assert.Equal(422, exception.StatusCode);
    }

    [Fact]
    public async Task ProducerCollectionsMustAlreadyBeCanonical()
    {
        var service = new ApplyCatalogListingPromotionEligibilityService(
            new UnexpectedStore(),
            new UnexpectedReconciler(),
            new FixedIdSource(
                Guid.Parse("0198ff00-0000-7000-8000-000000000090")),
            new FixedClock(ReceivedAtUtc));
        var invalidEvent = CreateEvent() with
        {
            CategoryKeys = ["recording-studio", "podcast-studio"],
        };

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() =>
            service.ApplyAsync(
                CreateMessage(invalidEvent),
                PromotionActor.Create(SystemActorId),
                CancellationToken.None));

        Assert.Equal(
            "PROMOTION_ELIGIBILITY_CATEGORIES_NOT_CANONICAL",
            exception.Code);
    }

    [Fact]
    public async Task BrokerIdentityMustMatchProducerEventIdentity()
    {
        var service = new ApplyCatalogListingPromotionEligibilityService(
            new UnexpectedStore(),
            new UnexpectedReconciler(),
            new FixedIdSource(
                Guid.Parse("0198ff00-0000-7000-8000-000000000090")),
            new FixedClock(ReceivedAtUtc));
        var message = CreateMessage(CreateEvent()) with
        {
            MessageId = Guid.Parse("0198ff00-0000-7000-8000-000000000099"),
        };

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() =>
            service.ApplyAsync(
                message,
                PromotionActor.Create(SystemActorId),
                CancellationToken.None));

        Assert.Equal(
            "PROMOTION_ELIGIBILITY_MESSAGE_ID_MISMATCH",
            exception.Code);
    }

    private static PromotionEligibilityProjectionMessage CreateMessage(
        CatalogListingPromotionEligibilityChanged integrationEvent) =>
        new(
            EventId,
            CatalogIntegrationEventContracts.ListingPromotionEligibilityChanged,
            PayloadDigest,
            "correlation-eligibility-1",
            CausationId: null,
            integrationEvent);

    private static CatalogListingPromotionEligibilityChanged CreateEvent() =>
        new(
            EventId,
            "berlin-recording-services",
            ListingId,
            RevisionId,
            IsPublished: true,
            IsArchived: false,
            HasBlockingDispute: false,
            HasVerifiedContact: true,
            VerifiedContactCapabilities:
            [
                CatalogPromotionContactCapabilities.Website,
                CatalogPromotionContactCapabilities.WhatsApp,
            ],
            CategoryKeys:
            [
                "podcast-studio",
                "recording-studio",
            ],
            DistrictKey: "mitte",
            EligibilityRevision: 1,
            OccurredAtUtc);

    private sealed class FixedClock(DateTimeOffset value) : IPromotionClock
    {
        public DateTimeOffset GetUtcNow() => value;
    }

    private sealed class CapturingStore(
        PromotionEligibilityProjectionApplyResult result =
            PromotionEligibilityProjectionApplyResult.Applied)
        : IPromotionEligibilityProjectionStore
    {
        public PromotionEligibilityProjectionChange? Change { get; private set; }

        public DateTimeOffset? ReceivedAtUtc { get; private set; }

        public Task<PromotionEligibilityProjectionApplyResult> ApplyAsync(
            PromotionEligibilityProjectionChange change,
            DateTimeOffset receivedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Change = change;
            ReceivedAtUtc = receivedAtUtc;
            return Task.FromResult(result);
        }
    }

    private sealed class FixedIdSource(Guid id) : IPromotionIdSource
    {
        public Guid CreateId() => id;
    }

    private sealed class CapturingReconciler : IPromotionEligibilityPlacementReconciler
    {
        public int CallCount { get; private set; }

        public ListingPromotionEligibility? Eligibility { get; private set; }

        public PromotionCommandContext? CommandContext { get; private set; }

        public DateTimeOffset? ChangedAtUtc { get; private set; }

        public IPromotionIdSource? IdSource { get; private set; }

        public Task<int> PauseIneligiblePlacementsAsync(
            ListingPromotionEligibility eligibility,
            PromotionCommandContext commandContext,
            DateTimeOffset changedAtUtc,
            IPromotionIdSource idSource,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Eligibility = eligibility;
            CommandContext = commandContext;
            ChangedAtUtc = changedAtUtc;
            IdSource = idSource;
            return Task.FromResult(0);
        }
    }

    private sealed class UnexpectedReconciler : IPromotionEligibilityPlacementReconciler
    {
        public Task<int> PauseIneligiblePlacementsAsync(
            ListingPromotionEligibility eligibility,
            PromotionCommandContext commandContext,
            DateTimeOffset changedAtUtc,
            IPromotionIdSource idSource,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Invalid producer input must be rejected before placement reconciliation.");
    }

    private sealed class UnexpectedStore : IPromotionEligibilityProjectionStore
    {
        public Task<PromotionEligibilityProjectionApplyResult> ApplyAsync(
            PromotionEligibilityProjectionChange change,
            DateTimeOffset receivedAtUtc,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Invalid producer input must be rejected before persistence.");
    }
}
