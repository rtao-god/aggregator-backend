using Aggregator.Promotion.Application;
using Aggregator.Promotion.Domain;

namespace Promotion.Application.Tests;

public sealed class PromotionScheduledPlacementPolicyTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 9, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ActivationAtUtc =
        Timestamp.AddHours(1);
    private static readonly Guid ActorId =
        Guid.Parse("0198ff10-0000-7000-8000-000000000001");

    [Fact]
    public void MissingEligibilityProjectionPausesScheduledPlacement()
    {
        var placement = CreateScheduledPlacement();

        var changed = PromotionScheduledPlacementPolicy.Synchronize(
            placement,
            CreateEntitlement(),
            CreateProduct(),
            null,
            ActorId,
            ActivationAtUtc);

        Assert.True(changed);
        Assert.Equal(SponsoredPlacementState.Paused, placement.State);
        Assert.False(placement.ConsumesCapacity);
        Assert.Contains(
            "eligibility projection is unavailable",
            placement.AuditReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IneligibleCatalogStatePausesScheduledPlacement()
    {
        var placement = CreateScheduledPlacement();
        var eligibility = ListingPromotionEligibility.Create(
            "berlin-recording-services",
            placement.ListingId,
            isPublished: false,
            isArchived: true,
            hasBlockingDispute: false,
            hasVerifiedContact: false,
            contactCapabilities: [],
            categoryKeys: [],
            districtKey: null,
            sourceRevision: 2,
            changedAtUtc: ActivationAtUtc);

        var changed = PromotionScheduledPlacementPolicy.Synchronize(
            placement,
            CreateEntitlement(),
            CreateProduct(),
            eligibility,
            ActorId,
            ActivationAtUtc);

        Assert.True(changed);
        Assert.Equal(SponsoredPlacementState.Paused, placement.State);
        Assert.Contains(
            "PROMOTION_LISTING_INELIGIBLE",
            placement.AuditReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IneffectiveEntitlementPausesScheduledPlacement()
    {
        var placement = CreateScheduledPlacement();
        var entitlement = CreateEntitlement();
        entitlement.Pause(
            entitlement.AggregateRevision,
            ActorId,
            "contract paused",
            Timestamp.AddMinutes(30));

        var changed = PromotionScheduledPlacementPolicy.Synchronize(
            placement,
            entitlement,
            CreateProduct(),
            CreateEligibility(placement.ListingId),
            ActorId,
            ActivationAtUtc);

        Assert.True(changed);
        Assert.Equal(SponsoredPlacementState.Paused, placement.State);
        Assert.Contains(
            "entitlement revision",
            placement.AuditReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledPlacementActivatesOnlyWithCurrentEligibility()
    {
        var placement = CreateScheduledPlacement();

        var changed = PromotionScheduledPlacementPolicy.Synchronize(
            placement,
            CreateEntitlement(),
            CreateProduct(),
            CreateEligibility(placement.ListingId),
            ActorId,
            ActivationAtUtc);

        Assert.True(changed);
        Assert.Equal(SponsoredPlacementState.Active, placement.State);
        Assert.True(placement.ConsumesCapacity);
        Assert.Equal(
            "owner-scheduled placement transition",
            placement.AuditReason);
    }

    [Fact]
    public void EligibilityRecoveryDoesNotResumePausedPlacement()
    {
        var placement = CreateScheduledPlacement();
        placement.Pause(
            placement.AggregateRevision,
            ActorId,
            "manual pause",
            Timestamp.AddMinutes(15));

        var changed = PromotionScheduledPlacementPolicy.Synchronize(
            placement,
            CreateEntitlement(),
            CreateProduct(),
            CreateEligibility(placement.ListingId),
            ActorId,
            ActivationAtUtc);

        Assert.False(changed);
        Assert.Equal(SponsoredPlacementState.Paused, placement.State);
        Assert.Equal("manual pause", placement.AuditReason);
    }

    private static PromotionProduct CreateProduct() =>
        PromotionProduct.Create(
            Guid.Parse("0198ff10-0000-7000-8000-000000000010"),
            "featured-listing",
            Guid.Parse("0198ff10-0000-7000-8000-000000000011"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["de-DE"] = "Gesponserter Eintrag",
                ["en-GB"] = "Sponsored listing",
            },
            [
                PromotionPresentationFeature.FeaturedListing,
                PromotionPresentationFeature.SponsoredSlot,
            ],
            requiresVerifiedContact: true,
            requiredContactCapability: "website",
            ActorId,
            Timestamp,
            Digest('a'));

    private static PromotionEntitlement CreateEntitlement() =>
        PromotionEntitlement.Grant(
            Guid.Parse("0198ff10-0000-7000-8000-000000000020"),
            Guid.Parse("0198ff10-0000-7000-8000-000000000021"),
            "featured-listing",
            PromotionEntitlementSourceType.ManualContract,
            "contract-berlin-activation",
            PromotionWindow.Create(Timestamp, Timestamp.AddDays(10)),
            ActorId,
            "signed commercial contract",
            Timestamp);

    private static SponsoredPlacement CreateScheduledPlacement()
    {
        var entitlement = CreateEntitlement();
        return SponsoredPlacement.Create(
            Guid.Parse("0198ff10-0000-7000-8000-000000000030"),
            Guid.Parse("0198ff10-0000-7000-8000-000000000031"),
            entitlement,
            CreateProduct(),
            CreateEligibility(entitlement.ListingId),
            "berlin-recording-services",
            PlacementScopeType.Category,
            "recording-studio",
            ["de-DE"],
            PromotionWindow.Create(ActivationAtUtc, Timestamp.AddDays(2)),
            priorityBand: 100,
            capacitySlot: 1,
            presentationLabelKey: "sponsored",
            ActorId,
            "placement contract",
            Timestamp,
            Digest('b'));
    }

    private static ListingPromotionEligibility CreateEligibility(Guid listingId) =>
        ListingPromotionEligibility.Create(
            "berlin-recording-services",
            listingId,
            isPublished: true,
            isArchived: false,
            hasBlockingDispute: false,
            hasVerifiedContact: true,
            contactCapabilities: ["website"],
            categoryKeys: ["recording-studio"],
            districtKey: "mitte",
            sourceRevision: 1,
            changedAtUtc: Timestamp);

    private static string Digest(char value) => new(value, 64);
}
