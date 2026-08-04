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
        var service = new PromotionProductService(
            repository,
            new QueuePromotionIdSource(
                Id(10),
                Id(11),
                Id(12),
                Id(13)),
            new FixedPromotionClock(Timestamp));
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
        var repository = ConfiguredRepository();
        var clock = new FixedPromotionClock(Timestamp);
        var ids = new QueuePromotionIdSource(Id(20), Id(21), Id(22), Id(23), Id(24));
        var entitlementService = new PromotionEntitlementService(repository, ids, clock);
        var placementService = new PromotionPlacementService(repository, ids, clock);
        var product = repository.Products.Values.Single();

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
            PlacementRequest(entitlement.Response.Id, "category placement"),
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
        var repository = ConfiguredRepository();
        var product = repository.Products.Values.Single();
        var firstEntitlement = Entitlement(Id(30), product.Key);
        var secondEntitlement = Entitlement(Id(31), product.Key);
        repository.Entitlements.Add(firstEntitlement.Id, firstEntitlement);
        repository.Entitlements.Add(secondEntitlement.Id, secondEntitlement);
        var service = new PromotionPlacementService(
            repository,
            new QueuePromotionIdSource(Id(32), Id(33), Id(34), Id(35), Id(36), Id(37)),
            new FixedPromotionClock(Timestamp));
        var firstRequest = PlacementRequest(firstEntitlement.Id, "first placement");
        _ = await service.CreateAsync(
            firstRequest,
            CommandContext,
            "placement-one",
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() => service.CreateAsync(
            firstRequest with
            {
                EntitlementId = secondEntitlement.Id,
                AuditReason = "conflicting placement",
            },
            CommandContext,
            "placement-two",
            CancellationToken.None));

        Assert.Equal("PROMOTION_CAPACITY_CONFLICT", exception.Code);
        Assert.Single(repository.Placements);
        Assert.Single(
            repository.Outbox,
            message => message.EventType == PromotionIntegrationEventTypes.PlacementChanged);
    }

    [Fact]
    public async Task MissingEligibilityProjectionFailsUnavailable()
    {
        var repository = new RecordingPromotionRepository();
        var product = SponsoredProduct();
        repository.Products.Add(product.Id, product);
        var entitlement = Entitlement(Id(40), product.Key);
        repository.Entitlements.Add(entitlement.Id, entitlement);
        var service = new PromotionPlacementService(
            repository,
            new QueuePromotionIdSource(Id(41), Id(42), Id(43)),
            new FixedPromotionClock(Timestamp));

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() => service.CreateAsync(
            PlacementRequest(entitlement.Id, "missing eligibility proof") with
            {
                ScopeType = PlacementScopeTypeContract.Catalog,
                ScopeKey = "berlin-recording-services",
                EndsAtUtc = Timestamp.AddHours(1),
            },
            CommandContext,
            "missing-eligibility",
            CancellationToken.None));

        Assert.Equal("PROMOTION_ELIGIBILITY_PROJECTION_UNAVAILABLE", exception.Code);
        Assert.Equal(503, exception.StatusCode);
    }

    private static RecordingPromotionRepository ConfiguredRepository()
    {
        var repository = new RecordingPromotionRepository();
        var product = SponsoredProduct();
        repository.Products.Add(product.Id, product);
        repository.Eligibility[("berlin-recording-services", ListingId())] = EligibleListing();
        return repository;
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
            [
                PromotionPresentationFeatureContract.FeaturedListing,
                PromotionPresentationFeatureContract.SponsoredSlot,
            ],
            RequiresVerifiedContact: true,
            RequiredContactCapability: "website");

    private static CreateSponsoredPlacementRequest PlacementRequest(
        Guid entitlementId,
        string auditReason) =>
        new(
            entitlementId,
            "berlin-recording-services",
            PlacementScopeTypeContract.Category,
            "recording-studio",
            ["de-DE", "en-GB"],
            Timestamp,
            Timestamp.AddDays(2),
            PriorityBand: 50,
            CapacitySlot: 1,
            PresentationLabelKey: "sponsored",
            AuditReason: auditReason);

    private static PromotionProduct SponsoredProduct() =>
        PromotionProduct.Create(
            Id(100),
            "featured-listing",
            Id(101),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["de-DE"] = "Hervorgehobener Eintrag",
                ["en-GB"] = "Featured listing",
            },
            [PromotionPresentationFeature.FeaturedListing, PromotionPresentationFeature.SponsoredSlot],
            requiresVerifiedContact: true,
            requiredContactCapability: "website",
            Id(102),
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
            Id(103),
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

    private static Guid ListingId() => Id(200);

    private static Guid Id(int suffix) =>
        Guid.Parse($"0198b200-0000-7000-8000-{suffix:D12}");

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

        public Task<PromotionProduct?> GetProductAsync(Guid productId, CancellationToken cancellationToken) =>
            ReadAsync(Products, productId, cancellationToken);

        public Task<PromotionProduct?> GetProductByKeyAsync(
            string productKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Products.Values.SingleOrDefault(product =>
                string.Equals(product.Key, productKey, StringComparison.Ordinal)));
        }

        public Task<PromotionEntitlement?> GetEntitlementAsync(
            Guid entitlementId,
            CancellationToken cancellationToken) =>
            ReadAsync(Entitlements, entitlementId, cancellationToken);

        public Task<SponsoredPlacement?> GetPlacementAsync(
            Guid placementId,
            CancellationToken cancellationToken) =>
            ReadAsync(Placements, placementId, cancellationToken);

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
            CancellationToken cancellationToken) =>
            SaveAsync(
                product,
                Products,
                addOnly: true,
                commandIdentity,
                commandContext,
                outboxMessage: null,
                cancellationToken);

        public Task<PromotionCommandResult<PromotionProduct>> SaveProductAsync(
            PromotionProduct product,
            long expectedStoredAggregateRevision,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            CancellationToken cancellationToken)
        {
            _ = expectedStoredAggregateRevision;
            return SaveAsync(
                product,
                Products,
                addOnly: false,
                commandIdentity,
                commandContext,
                outboxMessage: null,
                cancellationToken);
        }

        public Task<PromotionCommandResult<PromotionEntitlement>> AddEntitlementAsync(
            PromotionEntitlement entitlement,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            PromotionOutboxMessage outboxMessage,
            CancellationToken cancellationToken) =>
            SaveAsync(
                entitlement,
                Entitlements,
                addOnly: true,
                commandIdentity,
                commandContext,
                outboxMessage,
                cancellationToken);

        public Task<PromotionCommandResult<PromotionEntitlement>> SaveEntitlementAsync(
            PromotionEntitlement entitlement,
            long expectedStoredAggregateRevision,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            PromotionOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            _ = expectedStoredAggregateRevision;
            return SaveAsync(
                entitlement,
                Entitlements,
                addOnly: false,
                commandIdentity,
                commandContext,
                outboxMessage,
                cancellationToken);
        }

        public Task<PromotionCommandResult<SponsoredPlacement>> AddPlacementAsync(
            SponsoredPlacement placement,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            PromotionOutboxMessage outboxMessage,
            CancellationToken cancellationToken) =>
            SaveAsync(
                placement,
                Placements,
                addOnly: true,
                commandIdentity,
                commandContext,
                outboxMessage,
                cancellationToken);

        public Task<PromotionCommandResult<SponsoredPlacement>> SavePlacementAsync(
            SponsoredPlacement placement,
            long expectedStoredAggregateRevision,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            PromotionOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            _ = expectedStoredAggregateRevision;
            return SaveAsync(
                placement,
                Placements,
                addOnly: false,
                commandIdentity,
                commandContext,
                outboxMessage,
                cancellationToken);
        }

        public Task UpsertEligibilityAsync(
            ListingPromotionEligibility eligibility,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Eligibility[(eligibility.CatalogKey, eligibility.ListingId)] = eligibility;
            return Task.CompletedTask;
        }

        private static Task<TAggregate?> ReadAsync<TAggregate>(
            IReadOnlyDictionary<Guid, TAggregate> values,
            Guid id,
            CancellationToken cancellationToken)
            where TAggregate : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.TryGetValue(id, out var value);
            return Task.FromResult(value);
        }

        private Task<PromotionCommandResult<TAggregate>> SaveAsync<TAggregate>(
            TAggregate aggregate,
            IDictionary<Guid, TAggregate> values,
            bool addOnly,
            PromotionCommandIdentity commandIdentity,
            PromotionCommandContext commandContext,
            PromotionOutboxMessage? outboxMessage,
            CancellationToken cancellationToken)
            where TAggregate : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = commandContext;
            if (TryReplay(commandIdentity, out TAggregate? replay))
            {
                return Task.FromResult(new PromotionCommandResult<TAggregate>(replay, true));
            }

            var id = aggregate switch
            {
                PromotionProduct product => product.Id,
                PromotionEntitlement entitlement => entitlement.Id,
                SponsoredPlacement placement => placement.Id,
                _ => throw new InvalidOperationException("Unsupported Promotion test aggregate."),
            };
            if (addOnly)
            {
                Assert.True(values.TryAdd(id, aggregate));
            }
            else
            {
                values[id] = aggregate;
            }

            if (outboxMessage is not null)
            {
                Outbox.Add(outboxMessage);
            }

            _commands.Add(
                (commandIdentity.Scope, commandIdentity.Key),
                new StoredCommand(commandIdentity.RequestDigest, aggregate));
            return Task.FromResult(new PromotionCommandResult<TAggregate>(aggregate, false));
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

        private sealed record StoredCommand(string RequestDigest, object Aggregate);
    }
}
