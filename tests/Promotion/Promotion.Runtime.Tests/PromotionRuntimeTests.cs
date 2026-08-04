using Aggregator.Promotion.Application;
using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Domain;
using Aggregator.Promotion.Infrastructure;
using Aggregator.Promotion.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Promotion.Runtime.Tests;

public sealed class PromotionRuntimeTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CampaignCannotActivateWithoutEveryEligibilityOwner()
    {
        var campaign = CreateCampaign();

        var exception = Assert.Throws<PromotionCampaignException>(() =>
            campaign.Activate(
                productRevisionActive: true,
                entitlementActive: false,
                listingEligible: true,
                expectedAggregateRevision: 1,
                changedAtUtc: Now));

        Assert.Equal("PROMOTION_ACTIVATION_NOT_ELIGIBLE", exception.Code);
        Assert.Equal(PromotionCampaignState.Draft, campaign.State);
    }

    [Fact]
    public void CampaignRejectsStaleAggregateRevision()
    {
        var campaign = CreateCampaign();
        campaign.Activate(
            productRevisionActive: true,
            entitlementActive: true,
            listingEligible: true,
            expectedAggregateRevision: 1,
            changedAtUtc: Now);

        var exception = Assert.Throws<PromotionCampaignException>(() =>
            campaign.Suspend(
                "manual review",
                expectedAggregateRevision: 1,
                changedAtUtc: Now));

        Assert.Equal("PROMOTION_REVISION_CONFLICT", exception.Code);
    }

    [Fact]
    public async Task ExactCreateReplayReturnsOriginalCampaignIdentity()
    {
        var store = new InMemoryPromotionStore();
        var eligibility = new FixedEligibilityReader();
        var service = new PromotionCampaignService(
            store,
            eligibility,
            new MutableTimeProvider(Now));
        var request = CreateRequest();

        var created = await service.CreateAsync(
            request,
            "campaign-create-1",
            "promotion-test",
            CancellationToken.None);
        var replayed = await service.CreateAsync(
            request,
            "campaign-create-1",
            "promotion-test",
            CancellationToken.None);

        Assert.False(created.Replayed);
        Assert.True(replayed.Replayed);
        Assert.Equal(created.Id, replayed.Id);
        Assert.Equal(PromotionCampaignStateContract.Draft, replayed.State);
        Assert.Equal("sponsored", replayed.Disclosure);
        Assert.Single(store.Campaigns);
    }

    [Fact]
    public async Task ActivePlacementIsExplicitlySponsoredAndNeverCarriesOrganicScore()
    {
        var store = new InMemoryPromotionStore();
        var eligibility = new FixedEligibilityReader();
        var service = new PromotionCampaignService(
            store,
            eligibility,
            new MutableTimeProvider(Now));
        var created = await service.CreateAsync(
            CreateRequest(),
            "campaign-create-2",
            "promotion-test",
            CancellationToken.None);
        var activated = await service.ActivateAsync(
            created.Id,
            new PromotionCampaignRevisionRequest(created.AggregateRevision),
            "campaign-activate-2",
            "promotion-test",
            CancellationToken.None);

        var placement = await service.ReadSponsoredPlacementAsync(
            "berlin",
            "search-top",
            Now,
            limit: 20,
            CancellationToken.None);

        Assert.Equal(PromotionCampaignStateContract.Active, activated.State);
        var item = Assert.Single(placement.Items);
        Assert.Equal(created.Id, item.CampaignId);
        Assert.Equal("sponsored", item.Disclosure);
        Assert.DoesNotContain(
            typeof(SponsoredPlacementItem).GetProperties(),
            property => property.Name.Contains("Organic", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Rank", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Score", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PlacementCapacityIsReservedAcrossOverlappingWindows()
    {
        var store = new InMemoryPromotionStore();
        var eligibility = new FixedEligibilityReader(capacityLimit: 3);
        var service = new PromotionCampaignService(
            store,
            eligibility,
            new MutableTimeProvider(Now));
        var request = CreateRequest() with { CapacityUnits = 2 };
        await service.CreateAsync(
            request,
            "capacity-first",
            "promotion-test",
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<PromotionApplicationException>(() =>
            service.CreateAsync(
                request with { ListingId = Guid.CreateVersion7() },
                "capacity-second",
                "promotion-test",
                CancellationToken.None));

        Assert.Equal("PROMOTION_PLACEMENT_CAPACITY_EXCEEDED", exception.Code);
    }

    [Fact]
    public async Task ExpiryWorkerCompletesExactEndedCampaign()
    {
        var store = new InMemoryPromotionStore();
        var eligibility = new FixedEligibilityReader();
        var clock = new MutableTimeProvider(Now);
        var service = new PromotionCampaignService(store, eligibility, clock);
        var created = await service.CreateAsync(
            CreateRequest(),
            "expiry-create",
            "promotion-test",
            CancellationToken.None);
        await service.ActivateAsync(
            created.Id,
            new PromotionCampaignRevisionRequest(created.AggregateRevision),
            "expiry-activate",
            "promotion-test",
            CancellationToken.None);
        clock.UtcNow = Now + TimeSpan.FromHours(3);
        var completion = new CompleteExpiredPromotionCampaignsService(store, clock);

        var count = await completion.CompleteAsync(100, CancellationToken.None);
        var completed = await service.ReadAsync(created.Id, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(PromotionCampaignStateContract.Completed, completed.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void WorkerRejectsUnsafeCompletionBatchSize(int batchSize)
    {
        var options = new PromotionWorkerOptions
        {
            CompletionBatchSize = batchSize,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void PersistenceModelOwnsRevisionedCampaignsAndImmutableCommandResults()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var campaign = FindTable(model, "promotion", "campaign");
        var command = FindTable(model, "promotion_operations", "command_result");

        var revision = campaign.FindProperty("AggregateRevision");
        Assert.NotNull(revision);
        Assert.True(revision.IsConcurrencyToken);
        Assert.Contains(
            campaign.GetCheckConstraints(),
            constraint => constraint.Name == "ck_promotion_campaign_suspension_reason");
        Assert.DoesNotContain(
            campaign.GetProperties(),
            property => property.Name.Contains("Organic", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Rank", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Score", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            ["Scope", "Key"],
            command.FindPrimaryKey()!.Properties.Select(property => property.Name).ToArray());
        Assert.Contains(
            command.GetCheckConstraints(),
            constraint => constraint.Name == "ck_promotion_command_result_document");
    }

    private static PromotionCampaign CreateCampaign() =>
        PromotionCampaign.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "berlin",
            "search-top",
            capacityUnits: 1,
            startsAtUtc: Now - TimeSpan.FromHours(1),
            endsAtUtc: Now + TimeSpan.FromHours(2),
            createdAtUtc: Now - TimeSpan.FromHours(2));

    private static CreatePromotionCampaignRequest CreateRequest() =>
        new(
            Guid.Parse("019b9d00-0000-7000-8000-000000000101"),
            Guid.Parse("019b9d00-0000-7000-8000-000000000102"),
            Guid.Parse("019b9d00-0000-7000-8000-000000000103"),
            "berlin",
            "search-top",
            CapacityUnits: 1,
            StartsAtUtc: Now - TimeSpan.FromHours(1),
            EndsAtUtc: Now + TimeSpan.FromHours(2));

    private static PromotionRuntimeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PromotionRuntimeDbContext>()
            .UseNpgsql("Host=localhost;Database=promotion_db;Username=promotion_app;Password=test")
            .Options;
        return new PromotionRuntimeDbContext(options);
    }

    private static IEntityType FindTable(IModel model, string schema, string tableName) =>
        model.GetEntityTypes().Single(entity =>
            string.Equals(entity.GetSchema(), schema, StringComparison.Ordinal) &&
            string.Equals(entity.GetTableName(), tableName, StringComparison.Ordinal));

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = value;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FixedEligibilityReader(int capacityLimit = 10) : IPromotionEligibilityReader
    {
        public Task<PromotionEligibilitySnapshot?> ReadAsync(
            Guid productRevisionId,
            Guid entitlementId,
            Guid listingId,
            string catalogKey,
            string placementKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<PromotionEligibilitySnapshot?>(
                new PromotionEligibilitySnapshot(
                    productRevisionId,
                    ProductRevisionActive: true,
                    entitlementId,
                    EntitlementActive: true,
                    listingId,
                    ListingEligible: true,
                    catalogKey,
                    placementKey,
                    capacityLimit,
                    ProjectionRevision: 1));
        }
    }

    private sealed class InMemoryPromotionStore :
        IPromotionCampaignStore,
        IPromotionCommandResultReader
    {
        private readonly Dictionary<Guid, PromotionCampaignSnapshot> _campaigns = [];
        private readonly Dictionary<(string Scope, string Key), (string Digest, PromotionCampaignSnapshot Result)> _commands = [];

        public IReadOnlyCollection<PromotionCampaignSnapshot> Campaigns => _campaigns.Values;

        public Task<PromotionCampaignCommandResult> CreateAsync(
            PromotionCampaign campaign,
            int placementCapacityLimit,
            PromotionCommandIdentity commandIdentity,
            string callerIdentity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var replay = ReadReplay(commandIdentity);
            if (replay is not null)
            {
                return Task.FromResult(new PromotionCampaignCommandResult(replay, Replayed: true));
            }

            var reserved = _campaigns.Values
                .Where(existing =>
                    existing.CatalogKey == campaign.CatalogKey &&
                    existing.PlacementKey == campaign.PlacementKey &&
                    existing.State is PromotionCampaignState.Draft or PromotionCampaignState.Active or PromotionCampaignState.Suspended &&
                    existing.StartsAtUtc < campaign.EndsAtUtc &&
                    existing.EndsAtUtc > campaign.StartsAtUtc)
                .Sum(existing => existing.CapacityUnits);
            if (reserved + campaign.CapacityUnits > placementCapacityLimit)
            {
                throw new PromotionApplicationException(
                    "Promotion.Capacity",
                    "PROMOTION_PLACEMENT_CAPACITY_EXCEEDED",
                    409,
                    "The requested campaign exceeds placement capacity.",
                    "Reduce the requested capacity or choose another window.");
            }

            var snapshot = PromotionCampaignSnapshot.From(campaign);
            _campaigns.Add(snapshot.Id, snapshot);
            SaveCommand(commandIdentity, snapshot);
            return Task.FromResult(new PromotionCampaignCommandResult(snapshot, Replayed: false));
        }

        public Task<PromotionCampaignSnapshot?> ReadAsync(
            Guid campaignId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _campaigns.TryGetValue(campaignId, out var campaign)
                    ? campaign
                    : null);
        }

        public Task<PromotionCampaignCommandResult> SaveAsync(
            PromotionCampaign campaign,
            long expectedStoredAggregateRevision,
            PromotionCommandIdentity commandIdentity,
            string callerIdentity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var replay = ReadReplay(commandIdentity);
            if (replay is not null)
            {
                return Task.FromResult(new PromotionCampaignCommandResult(replay, Replayed: true));
            }

            var current = _campaigns[campaign.Id];
            if (current.AggregateRevision != expectedStoredAggregateRevision)
            {
                throw new PromotionApplicationException(
                    "Promotion.Campaigns",
                    "PROMOTION_REVISION_CONFLICT",
                    409,
                    "The campaign changed before persistence.",
                    "Reload the campaign and retry.");
            }

            var snapshot = PromotionCampaignSnapshot.From(campaign);
            _campaigns[campaign.Id] = snapshot;
            SaveCommand(commandIdentity, snapshot);
            return Task.FromResult(new PromotionCampaignCommandResult(snapshot, Replayed: false));
        }

        public Task<IReadOnlyList<PromotionCampaignSnapshot>> ReadActiveAsync(
            string catalogKey,
            string placementKey,
            DateTimeOffset effectiveAtUtc,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PromotionCampaignSnapshot> result = _campaigns.Values
                .Where(campaign =>
                    campaign.CatalogKey == catalogKey &&
                    campaign.PlacementKey == placementKey &&
                    campaign.State == PromotionCampaignState.Active &&
                    campaign.StartsAtUtc <= effectiveAtUtc &&
                    campaign.EndsAtUtc > effectiveAtUtc)
                .OrderBy(campaign => campaign.StartsAtUtc)
                .ThenBy(campaign => campaign.Id)
                .Take(limit)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<PromotionCampaignSnapshot>> ReadExpiredAsync(
            DateTimeOffset effectiveAtUtc,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PromotionCampaignSnapshot> result = _campaigns.Values
                .Where(campaign =>
                    campaign.State is PromotionCampaignState.Active or PromotionCampaignState.Suspended &&
                    campaign.EndsAtUtc <= effectiveAtUtc)
                .OrderBy(campaign => campaign.EndsAtUtc)
                .ThenBy(campaign => campaign.Id)
                .Take(limit)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<PromotionCampaignSnapshot?> ReadCommandResultAsync(
            PromotionCommandIdentity identity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadReplay(identity));
        }

        private PromotionCampaignSnapshot? ReadReplay(PromotionCommandIdentity identity)
        {
            if (!_commands.TryGetValue((identity.Scope, identity.Key), out var command))
            {
                return null;
            }

            if (!string.Equals(command.Digest, identity.RequestDigest, StringComparison.Ordinal))
            {
                throw new PromotionApplicationException(
                    "Promotion.Commands",
                    "PROMOTION_IDEMPOTENCY_DIGEST_CONFLICT",
                    409,
                    "The command key was used for another request.",
                    "Replay the exact request or use a new key.");
            }

            return command.Result;
        }

        private void SaveCommand(
            PromotionCommandIdentity identity,
            PromotionCampaignSnapshot result) =>
            _commands.Add((identity.Scope, identity.Key), (identity.RequestDigest, result));
    }
}
