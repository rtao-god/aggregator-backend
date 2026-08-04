using Aggregator.Promotion.Application;
using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Domain;

namespace Promotion.Application.Tests;

public sealed class PromotionApplicationServiceTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly PromotionCommandContext CommandContext =
        PromotionCommandContext.Start(
            PromotionActor.Create(Guid.Parse("0198b200-0000-7000-8000-000000000001")),
            "promotion-test-correlation");

    [Fact]
    public async Task ProductCreateReplayReturnsExactFirstIdentity()
    {
        var repository = new RecordingPromotionRepository();
        var ids = new QueuePromotionIdSource(
            Guid.Parse("0198b200-0000-7000-8000-000000000010"),
            Guid.Parse("0198b200-0000-7000-8000-000000000011"),
            Guid.Parse("0198b200-0000-7000-8000-000000000012"),
            Guid.Parse("0198b200-0000-7000-8000-000000000013"));
        var service = new PromotionProductService(repository, ids, new FixedPromotionClock(Timestamp));
        var request = ProductRequest();

        var first = await service.CreateAsync(
            request,
            CommandContext,
            "product-create-key",
            CancellationToken.None);
        var replay = await service.CreateAsync(
            request,
            CommandContext,
            "product-create-key",
            CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Response.Id, replay.Response.Id);
        Assert.Equal(first.Response.CurrentRevision.Id, replay.Response.CurrentRevision.Id);
        Assert.Single(repository.Products);
    }

    [Fact]
    public async Task EntitlementAndPlacementProduceSeparateOutboxEvents()
    {
        var repository = new RecordingPromotionRepository();
        var product = SponsoredProduct();
        repository.Products.Add(product.Id, product);
        repository.Eligibility[("berlin-recording-services", ListingId())] = EligibleListing();
        var ids = new QueuePromotionIdSource(
            Guid.Parse("0198b200-0000-7000-8000-000000000020"),
            Guid.Parse("0198b200-0000-7000-8000-000000000021"),
            Guid.Parse("0198b200-0000-7000-8000-000000000022"),
            Guid.Parse("0198b200-0000-7000-8000-000000000023"),
            Guid.Parse("0198b200-0000-7000-8000-000000000024"));
        var clock = new FixedPromotionClock(Timestamp);
        var entitlementService = new PromotionEntitlementService(repository, ids, clock);
        var placementService = new PromotionPlacementService(repository, ids, clock);

        var entitlement = await entitlementService.GrantAsync(
            new GrantPromotionEntitlementRequest(
                ListingId(),
                product.Key,
                PromotionEntitlementSourceTypeContract.ManualContract,
                "contract-42",
                Timestamp,
                Timestamp.AddDays(7),
                "signed contract"),
            CommandContext,
            "entitlement-key",
            CancellationToken.None);
        var placement = await placementService.CreateAsync(
            new CreateSponsoredPlacementRequest(
                entitlement.Response.Id,
                "berlin-recording-services",
                PlacementScopeTypeContract.Category,
                "recording-studio",
                ["de-DE", "en-GB"],
                Timestamp,
                Timestamp.AddDays(2),
                PriorityBand: 50,
                CapacitySlot: 1,
                PresentationLabelKey: "sponsored",
                AuditReason: "category placement"),
            CommandContext,
            "placement-key",
            CancellationToken.None);

        Assert.Equal(PromotionEntitlementStateContract.Active, entitlement.Response.State);
        Assert.Equal(SponsoredPlacementStateContract.Active, placement.Response.State);
        Assert.Equal(Timestamp.AddDays(2), placement.Response.HardExpiryAtUtc);
        Assert.Collection(
            repository.Outbox,
            first => Assert.Equal(PromotionIntegrationEventTypes.EntitlementChanged, first.EventType),
            second => Assert.Equal(PromotionIntegrationEventTypes.PlacementChanged, second.EventType));
    }

    [Fact]
    public async Task ConflictingPlacementFailsBeforeSecondPersistence()
    {
        var repository = new RecordingPromotionRepository();
        var product = SponsoredProduct();
        repository.Products.Add(product.Id, product);
        repository.Eligibility[("berlin-recording-services", ListingId())] = EligibleListing();
        var firstEntitlement = Entitlement(
            Guid.Parse("0198b200-0000-7000-8000-000000000030"),
            product.Key);
        var secondEntitlement = Entitlement(
            Guid.Parse("0198b200-0000-7000-8000-000000000031"),
            product.Key);
        repository.Entitlements.Add(firstEntitlement.Id, firstEntitlement);
        repository.Entitlements.Add(secondEntitlement.Id, secondEntitlement);
        var ids = new QueuePromotionIdSource(
            Guid.Parse("0198b200-0000-7000-8000-000000000032"),
            Guid.Parse("0198b200-0000-7000-8000-000000000033"),
            Guid.Parse("0198b200-0000-7000-8000-000000000034"),
            Guid.Parse("0198b200-0000-7000-8000-000000000035"),
            Guid.Parse("0198b200-0000-7000-8000-000000000036"),
            Guid.Parse("0198b200-0000-7000-8000-000000000037"));
        var service = new PromotionPlacementService(
            repository,
            ids,
            new FixedPromotionClock(Timestamp));
        var request = new CreateSponsoredPlacementRequest(
            firstEntitlement.Id,
            "berlin-recording-services",
            PlacementScopeTypeContract.Category,
            "recording-studio",
            ["de-DE"],
            Timestamp,
            Timestamp.AddDays(2),
            PriorityBand: 10,
            CapacitySlot: 1,
            PresentationLabelKey: "sponsored",
            AuditReason: "first placement");
        _ = await service.CreateAsync(
            request,
            CommandContext,
            "placement-one",
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() => service.CreateAsync(
            request with
            {
                EntitlementId = secondEntitlement.Id,
                AuditReason = "conflicting placement",
            },
            CommandContext,
            "placement-two",
            CancellationToken.None));

        Assert.Equal("PROMOTION_CAPACITY_CONFLICT", exception.Code);
        Assert.Single(repository.Placements);
        Assert.Single(repository.Outbox.Where(message =>
            message.EventType == PromotionIntegrationEventTypes.PlacementChanged));
    }

    [Fact]
    public async Task MissingEligibilityProjectionFailsUnavailable()
    {
        var repository = new RecordingPromotionRepository();
        var product = SponsoredProduct();
        repository.Products.Add(product.Id, product);
        var entitlement = Entitlement(
            Guid.Parse("0198b200-0000-7000-8000-000000000040"),
            product.Key);
        repository.Entitlements.Add(entitlement.Id, entitlement);
        var service = new PromotionPlacementService(
            repository,
            new QueuePromotionIdSource(
                Guid.Parse("0198b200-0000-7000-8000-000000000041"),
                Guid.Parse("0198b200-0000-7000-8000-000000000042"),
                Guid.Parse("0198b200-0000-7000-8000-000000000043")),
            new FixedPromotionClock(Timestamp));

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() => service.CreateAsync(
            new CreateSponsoredPlacementRequest(
                entitlement.Id,
                "berlin-recording-services",
                PlacementScopeTypeContract.Catalog,
                "berlin-recording-services",
                ["de-DE"],
                Timestamp,
                Timestamp.AddHours(1),
                PriorityBand: 1,
                CapacitySlot: 1,
                PresentationLabelKey: "sponsored",
                AuditReason: "missing eligibility proof"),
            CommandContext,
            "missing-eligibility",
            CancellationToken.None));

        Assert.Equal("PROMOTION_ELIGIBILITY_PROJECTION_UNAVAILABLE", exception.Code);
        Assert.Equal(503, exception.StatusCode);
    }

    private static CreatePromotionProductRequest ProductRequest() =>
        new(
            PromotionContractIdentity.AdminApi,
            PromotionContractIdentity.AdminApiRevision,
            "featured-listing",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["de-DE"] = "Hervorgehobener Eintrag",
                ["en-GB"] = "Featured listing",
            },
            [PromotionPresentationFeatureContract.FeaturedListing, PromotionPresentationFeatureContract.SponsoredSlot],
            RequiresVerifiedContact: true,
            RequiredContactCapability: "website");

    private static PromotionProduct SponsoredProduct() =>
        PromotionProduct.Create(
            Guid.Parse("0198b200-0000-7000-8000-000000000100"),
            "featured-listing",
            Guid.Parse("0198b200-0000-7000-8000-000000000101"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["de-DE"] = "Hervorgehobener Eintrag",
                ["en-GB"] = "Featured listing",
            },
            [PromotionPresentationFeature.FeaturedListing, PromotionPresentationFeature.SponsoredSlot],
            requiresVerifiedContact: true,
            requiredContactCapability: "website",
            Guid.Parse("0198b200-0000-7000-8000-000000000102"),
            Timestamp,
            new string('a', 64));

    private static PromotionEntitlement Entitlement(Guid id, string productKey) =>
        PromotionEntitlement.Grant(
            id,
            ListingId(),
            productKey,
            PromotionEntitlementSourceType.ManualContract,
            $"contract-{id:N}",
            PromotionWindow.Create(Timestamp, Timestamp.AddDays(7)),
            Guid.Parse("0198b200-0000-7000-8000-000000000103"),
            "signed contract",
            Timestamp);

    private static ListingPromotionEligibility EligibleListing() =>
        ListingPromotionEligibility.Create(
            "berlin-recording-services",
            ListingId(),
            isPublished: true,
            isArchived: false,
            hasBlockingDispute: false,
            hasVerifiedContact: true,
            contactCapabilities: ["website"],
            categoryKeys: ["recording-studio"],
            districtKey: "mitte",
            sourceRevision: 1,
            Timestamp);

    private static Guid ListingId() =>
        Guid.Parse("0198b200-0000-7000-8000-000000000200");

    private sealed class FixedPromotionClock(DateTimeOffset value) : IPromotionClock
    {
        public DateTimeOffset GetUtcNow() => value;
    }

    private sealed class QueuePromotionIdSource(params Guid[] values) : IPromotionIdSource
    {
        private readonly Queue<Guid> _values = new(values);

        public Guid CreateId() =>
            _values.Count > 0
                ? _values.Dequeue()
                : throw new InvalidOperationException("Promotion test ID sequence is exhausted.");
    }

    private sealed class RecordingPromotionRepository : IPromotionRepository
    {
        private readonly Dictionary<(string Scope, string Key), StoredCommand> _commands = [];

        public Dictionary<Guid, PromotionProduct> Products { get; } = [];

        public Dictionary<Guid, PromotionEntitlement> Entitlements { get; } = [];

        public Dictionary<Guid, SponsoredPlacement> Placements { get; } = [];

        public Dictionary<(string CatalogKey, Guid ListingId), ListingPromotionEligibility> Eligibility { get; } = [];

        public List<PromotionOutboxMessage> Outbox { get; } = [];

        public Task<PromotionProduct?> GetProductAsync(Guid productId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Products.TryGetValue(productId, out var value);
            return Task.FromResult(value);
        }

        public Task<PromotionProduct?> GetProductByKeyAsync(
            string productKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = Products.Values.SingleOrDefault(product =>
                string.Equals(product.Key, productKey, StringComparison.Ordinal));
            return Task.FromResult(value);
        }

        public Task<PromotionEntitlement?> GetEntitlementAsync(
            Guid entitlementId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entitlements.TryGetValue(entitlementId, out var value);
            return Task.FromResult(value);
        }

        public Task<SponsoredPlacement?> GetPlacementAsync(
            Guid placementId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Placements.TryGetValue(placementId, out var value);
            return Task.FromResult(value);
        }

        public Task<ListingPromotionEligibility?> GetEligibilityAsync(
            string catalogKey,
            Guid listingId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Eligibility.TryGetValue((catalogKey, listingId), out var value);
            return Task.FromResult(value);
        }

        public Task<IReadOnlyList<PromotionEntitlement>> ListEntitlementsAsync(
            Guid listingId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PromotionEntitlement> values = Entitlements.Values
                .Where(item => item.ListingId == listingId)
                .ToArray();
            return Task.FromResult(values);
        }

        public Task<IReadOnlyList<SponsoredPlacement>> ListPlacementsAsync(
            string catalogKey,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = PromotionWindow.Create(fromUtc, toUtc);
            IReadOnlyList<SponsoredPlacement> values = Placements.Values
                .Where(item => string.Equals(
                    item.CurrentRevision.CatalogKey,
                    catalogKey,
                    StringComparison.Ordinal))
                .Where(item => item.CurrentRevision.EffectiveWindow.Overlaps(window))
                .ToArray();
            return Task.FromResult(values);
        }

        public Task<bool> HasPlacementConflictAsync(
            SponsoredPlacement candidate,
            Guid? excludedPlacementId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Placements.Values.Any(item =>
                item.Id != excludedPlacementId && candidate.Overlaps(item)));
        }

        public Task<PromotionCommandResult<PromotionProduct>> AddProductAsync(
            PromotionProduct product,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = commandContext;
            if (TryReplay(commandIdentity, out PromotionProduct? replay))
            {
                return Task.FromResult(new PromotionCommandResult<PromotionProduct>(replay, true));
            }

            Assert.True(Products.TryAdd(product.Id, product));
            Record(commandIdentity, product);
            return Task.FromResult(new PromotionCommandResult<PromotionProduct>(product, false));
        }

        public Task<PromotionCommandResult<PromotionProduct>> SaveProductAsync(
            PromotionProduct product,
            long expectedStoredAggregateRevision,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = expectedStoredAggregateRevision;
            _ = commandContext;
            if (TryReplay(commandIdentity, out PromotionProduct? replay))
            {
                return Task.FromResult(new PromotionCommandResult<PromotionProduct>(replay, true));
            }

            Products[product.Id] = product;
            Record(commandIdentity, product);
            return Task.FromResult(new PromotionCommandResult<PromotionProduct>(product, false));
        }

        public Task<PromotionCommandResult<PromotionEntitlement>> AddEntitlementAsync(
            PromotionEntitlement entitlement,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            PromotionOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = commandContext;
            if (TryReplay(commandIdentity, out PromotionEntitlement? replay))
            {
                return Task.FromResult(new PromotionCommandResult<PromotionEntitlement>(replay, true));
            }

            Assert.True(Entitlements.TryAdd(entitlement.Id, entitlement));
            Outbox.Add(outboxMessage);
            Record(commandIdentity, entitlement);
            return Task.FromResult(new PromotionCommandResult<PromotionEntitlement>(entitlement, false));
        }

        public Task<PromotionCommandResult<PromotionEntitlement>> SaveEntitlementAsync(
            PromotionEntitlement entitlement,
            long expectedStoredAggregateRevision,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            PromotionOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = expectedStoredAggregateRevision;
            _ = commandContext;
            if (TryReplay(commandIdentity, out PromotionEntitlement? replay))
            {
                return Task.FromResult(new PromotionCommandResult<PromotionEntitlement>(replay, true));
            }

            Entitlements[entitlement.Id] = entitlement;
            Outbox.Add(outboxMessage);
            Record(commandIdentity, entitlement);
            return Task.FromResult(new PromotionCommandResult<PromotionEntitlement>(entitlement, false));
        }

        public Task<PromotionCommandResult<SponsoredPlacement>> AddPlacementAsync(
            SponsoredPlacement placement,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            PromotionOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = commandContext;
            if (TryReplay(commandIdentity, out SponsoredPlacement? replay))
            {
                return Task.FromResult(new PromotionCommandResult<SponsoredPlacement>(replay, true));
            }

            Assert.True(Placements.TryAdd(placement.Id, placement));
            Outbox.Add(outboxMessage);
            Record(commandIdentity, placement);
            return Task.FromResult(new PromotionCommandResult<SponsoredPlacement>(placement, false));
        }

        public Task<PromotionCommandResult<SponsoredPlacement>> SavePlacementAsync(
            SponsoredPlacement placement,
            long expectedStoredAggregateRevision,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            PromotionOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = expectedStoredAggregateRevision;
            _ = commandContext;
            if (TryReplay(commandIdentity, out SponsoredPlacement? replay))
            {
                return Task.FromResult(new PromotionCommandResult<SponsoredPlacement>(replay, true));
            }

            Placements[placement.Id] = placement;
            Outbox.Add(outboxMessage);
            Record(commandIdentity, placement);
            return Task.FromResult(new PromotionCommandResult<SponsoredPlacement>(placement, false));
        }

        public Task UpsertEligibilityAsync(
            ListingPromotionEligibility eligibility,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Eligibility[(eligibility.CatalogKey, eligibility.ListingId)] = eligibility;
            return Task.CompletedTask;
        }

        private bool TryReplay<TAggregate>(
            PromotionCommandIdentity identity,
            out TAggregate? aggregate)
            where TAggregate : class
        {
            if (!_commands.TryGetValue((identity.Scope, identity.Key), out var existing))
            {
                aggregate = null;
                return false;
            }

            if (!string.Equals(existing.RequestDigest, identity.RequestDigest, StringComparison.Ordinal))
            {
                throw new PromotionApplicationException(
                    "Promotion.Commands",
                    "PROMOTION_IDEMPOTENCY_CONFLICT",
                    409,
                    "Promotion command key was already used with another request digest.",
                    "Use the original request or submit a new semantic idempotency key.");
            }

            aggregate = Assert.IsType<TAggregate>(existing.Aggregate);
            return true;
        }

        private void Record<TAggregate>(
            PromotionCommandIdentity identity,
            TAggregate aggregate)
            where TAggregate : class =>
            _commands.Add(
                (identity.Scope, identity.Key),
                new StoredCommand(identity.RequestDigest, aggregate));

        private sealed record StoredCommand(string RequestDigest, object Aggregate);
    }
}
