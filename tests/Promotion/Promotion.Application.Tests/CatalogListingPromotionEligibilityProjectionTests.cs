using Aggregator.Catalog.Contracts;
using Aggregator.Promotion.Application;

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
    private const string PayloadDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ExactCatalogEventBecomesOneAtomicProjectionChange()
    {
        var store = new CapturingStore();
        var service = new ApplyCatalogListingPromotionEligibilityService(
            store,
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
    }

    [Fact]
    public async Task UnpublishedEventCannotRetainPublicEligibilityFacts()
    {
        var service = new ApplyCatalogListingPromotionEligibilityService(
            new UnexpectedStore(),
            new FixedClock(ReceivedAtUtc));
        var invalidEvent = CreateEvent() with
        {
            PublishedListingRevisionId = null,
            IsPublished = false,
        };

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() =>
            service.ApplyAsync(
                CreateMessage(invalidEvent),
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
            new FixedClock(ReceivedAtUtc));
        var invalidEvent = CreateEvent() with
        {
            CategoryKeys = ["recording-studio", "podcast-studio"],
        };

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() =>
            service.ApplyAsync(
                CreateMessage(invalidEvent),
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
            new FixedClock(ReceivedAtUtc));
        var message = CreateMessage(CreateEvent()) with
        {
            MessageId = Guid.Parse("0198ff00-0000-7000-8000-000000000099"),
        };

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() =>
            service.ApplyAsync(message, CancellationToken.None));

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

    private sealed class CapturingStore : IPromotionEligibilityProjectionStore
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
            return Task.FromResult(PromotionEligibilityProjectionApplyResult.Applied);
        }
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
