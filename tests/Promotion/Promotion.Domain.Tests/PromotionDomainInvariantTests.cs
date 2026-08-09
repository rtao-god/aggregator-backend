using Aggregator.Promotion.Domain;

namespace Promotion.Domain.Tests;

public sealed class PromotionDomainInvariantTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProductRevisionIsImmutableAndAggregateRevisionAdvances()
    {
        var product = CreateSponsoredProduct();
        var firstRevision = product.CurrentRevision;

        var secondRevision = product.AddRevision(
            expectedAggregateRevision: 1,
            Guid.Parse("0198b100-0000-7000-8000-000000000003"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["de-DE"] = "Hervorgehobener Eintrag",
                ["en-GB"] = "Featured listing",
            },
            [PromotionPresentationFeature.FeaturedListing, PromotionPresentationFeature.SponsoredSlot],
            requiresVerifiedContact: true,
            requiredContactCapability: "website",
            Guid.Parse("0198b100-0000-7000-8000-000000000004"),
            Timestamp.AddMinutes(1),
            Digest('b'));

        Assert.Equal(1, firstRevision.RevisionNumber);
        Assert.Equal("Gesponserter Eintrag", firstRevision.DisplayNames["de-DE"]);
        Assert.Equal(2, secondRevision.RevisionNumber);
        Assert.Equal(2, product.AggregateRevision);
        Assert.Same(secondRevision, product.CurrentRevision);
    }

    [Fact]
    public void ProductRejectsContactCapabilityWithoutVerificationRequirement()
    {
        var exception = Assert.Throws<PromotionDomainException>(() => PromotionProduct.Create(
            Guid.Parse("0198b100-0000-7000-8000-000000000010"),
            "invalid-product",
            Guid.Parse("0198b100-0000-7000-8000-000000000011"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["de-DE"] = "Ungültig",
            },
            [PromotionPresentationFeature.SponsoredSlot],
            requiresVerifiedContact: false,
            requiredContactCapability: "website",
            Guid.Parse("0198b100-0000-7000-8000-000000000012"),
            Timestamp,
            Digest('c')));

        Assert.Equal("PROMOTION_PRODUCT_CONTACT_REQUIREMENT_INVALID", exception.Code);
    }

    [Fact]
    public void EntitlementLifecycleCannotResumeAfterRevocation()
    {
        var entitlement = CreateEntitlement();
        entitlement.Pause(
            expectedAggregateRevision: 1,
            Guid.Parse("0198b100-0000-7000-8000-000000000020"),
            "campaign paused",
            Timestamp.AddHours(1));
        entitlement.Resume(
            expectedAggregateRevision: 2,
            Guid.Parse("0198b100-0000-7000-8000-000000000021"),
            "campaign resumed",
            Timestamp.AddHours(2));
        entitlement.Revoke(
            expectedAggregateRevision: 3,
            Guid.Parse("0198b100-0000-7000-8000-000000000022"),
            "contract revoked",
            Timestamp.AddHours(3));

        var exception = Assert.Throws<PromotionDomainException>(() => entitlement.Resume(
            expectedAggregateRevision: 4,
            Guid.Parse("0198b100-0000-7000-8000-000000000023"),
            "invalid resume",
            Timestamp.AddHours(4)));

        Assert.Equal(PromotionEntitlementState.Revoked, entitlement.State);
        Assert.Equal("PROMOTION_ENTITLEMENT_TRANSITION_INVALID", exception.Code);
    }

    [Fact]
    public void IneligibleListingCannotCreateSponsoredPlacement()
    {
        var product = CreateSponsoredProduct();
        var entitlement = CreateEntitlement();
        var eligibility = ListingPromotionEligibility.Create(
            "berlin-recording-services",
            entitlement.ListingId,
            isPublished: false,
            isArchived: true,
            hasBlockingDispute: false,
            hasVerifiedContact: false,
            contactCapabilities: [],
            categoryKeys: ["recording-studio"],
            districtKey: "mitte",
            sourceRevision: 1,
            Timestamp);

        var exception = Assert.Throws<PromotionDomainException>(() => SponsoredPlacement.Create(
            Guid.Parse("0198b100-0000-7000-8000-000000000030"),
            Guid.Parse("0198b100-0000-7000-8000-000000000031"),
            entitlement,
            product,
            eligibility,
            "berlin-recording-services",
            PlacementScopeType.Category,
            "recording-studio",
            ["de-DE"],
            PromotionWindow.Create(Timestamp, Timestamp.AddDays(2)),
            priorityBand: 100,
            capacitySlot: 1,
            presentationLabelKey: "sponsored",
            Guid.Parse("0198b100-0000-7000-8000-000000000032"),
            "placement contract",
            Timestamp,
            Digest('d')));

        Assert.Equal("PROMOTION_LISTING_INELIGIBLE", exception.Code);
    }

    [Fact]
    public void CatalogEligibilityLossPausesAnActivePlacement()
    {
        var placement = CreatePlacement(
            Guid.Parse("0198b100-0000-7000-8000-000000000034"),
            Guid.Parse("0198b100-0000-7000-8000-000000000035"),
            capacitySlot: 1,
            locales: ["de-DE"],
            Timestamp,
            Timestamp.AddDays(2));
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
            Timestamp.AddMinutes(1));

        var changed = placement.PauseWhenCatalogIneligible(
            eligibility,
            CreateSponsoredProduct(),
            Guid.Parse("0198b100-0000-7000-8000-000000000036"),
            Timestamp.AddMinutes(1));

        Assert.True(changed);
        Assert.Equal(SponsoredPlacementState.Paused, placement.State);
        Assert.False(placement.ConsumesCapacity);
        Assert.Equal(2, placement.AggregateRevision);
        Assert.Contains(
            "PROMOTION_LISTING_INELIGIBLE",
            placement.AuditReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EligibilityRecoveryDoesNotAutoResumeAPausedPlacement()
    {
        var placement = CreatePlacement(
            Guid.Parse("0198b100-0000-7000-8000-000000000037"),
            Guid.Parse("0198b100-0000-7000-8000-000000000038"),
            capacitySlot: 1,
            locales: ["de-DE"],
            Timestamp,
            Timestamp.AddDays(2));
        var actorId = Guid.Parse("0198b100-0000-7000-8000-000000000039");
        placement.Pause(
            placement.AggregateRevision,
            actorId,
            "manual pause",
            Timestamp.AddMinutes(1));
        var eligibility = ListingPromotionEligibility.Create(
            "berlin-recording-services",
            placement.ListingId,
            isPublished: true,
            isArchived: false,
            hasBlockingDispute: false,
            hasVerifiedContact: true,
            contactCapabilities: ["website"],
            categoryKeys: ["recording-studio"],
            districtKey: "mitte",
            sourceRevision: 2,
            Timestamp.AddMinutes(2));

        var changed = placement.PauseWhenCatalogIneligible(
            eligibility,
            CreateSponsoredProduct(),
            actorId,
            Timestamp.AddMinutes(2));

        Assert.False(changed);
        Assert.Equal(SponsoredPlacementState.Paused, placement.State);
        Assert.Equal(2, placement.AggregateRevision);
        Assert.Equal("manual pause", placement.AuditReason);
    }

    [Fact]
    public void CapacityOverlapRequiresSameScopeSlotLocaleAndIntersectingTime()
    {
        var first = CreatePlacement(
            Guid.Parse("0198b100-0000-7000-8000-000000000040"),
            Guid.Parse("0198b100-0000-7000-8000-000000000041"),
            capacitySlot: 1,
            locales: ["de-DE"],
            Timestamp,
            Timestamp.AddDays(2));
        var conflicting = CreatePlacement(
            Guid.Parse("0198b100-0000-7000-8000-000000000042"),
            Guid.Parse("0198b100-0000-7000-8000-000000000043"),
            capacitySlot: 1,
            locales: ["de-DE", "en-GB"],
            Timestamp.AddDays(1),
            Timestamp.AddDays(3));
        var differentSlot = CreatePlacement(
            Guid.Parse("0198b100-0000-7000-8000-000000000044"),
            Guid.Parse("0198b100-0000-7000-8000-000000000045"),
            capacitySlot: 2,
            locales: ["de-DE"],
            Timestamp.AddDays(1),
            Timestamp.AddDays(3));
        var differentLocale = CreatePlacement(
            Guid.Parse("0198b100-0000-7000-8000-000000000046"),
            Guid.Parse("0198b100-0000-7000-8000-000000000047"),
            capacitySlot: 1,
            locales: ["fr-FR"],
            Timestamp.AddDays(1),
            Timestamp.AddDays(3));

        Assert.True(first.Overlaps(conflicting));
        Assert.False(first.Overlaps(differentSlot));
        Assert.False(first.Overlaps(differentLocale));
        Assert.Equal(Timestamp.AddDays(2), first.HardExpiryAtUtc);
    }

    private static PromotionProduct CreateSponsoredProduct() =>
        PromotionProduct.Create(
            Guid.Parse("0198b100-0000-7000-8000-000000000001"),
            "featured-listing",
            Guid.Parse("0198b100-0000-7000-8000-000000000002"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["de-DE"] = "Gesponserter Eintrag",
                ["en-GB"] = "Sponsored listing",
            },
            [PromotionPresentationFeature.FeaturedListing, PromotionPresentationFeature.SponsoredSlot],
            requiresVerifiedContact: true,
            requiredContactCapability: "website",
            Guid.Parse("0198b100-0000-7000-8000-000000000004"),
            Timestamp,
            Digest('a'));

    private static PromotionEntitlement CreateEntitlement() =>
        PromotionEntitlement.Grant(
            Guid.Parse("0198b100-0000-7000-8000-000000000100"),
            Guid.Parse("0198b100-0000-7000-8000-000000000101"),
            "featured-listing",
            PromotionEntitlementSourceType.ManualContract,
            "contract-berlin-001",
            PromotionWindow.Create(Timestamp, Timestamp.AddDays(10)),
            Guid.Parse("0198b100-0000-7000-8000-000000000102"),
            "signed commercial contract",
            Timestamp);

    private static SponsoredPlacement CreatePlacement(
        Guid placementId,
        Guid revisionId,
        int capacitySlot,
        IReadOnlyList<string> locales,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc)
    {
        var product = CreateSponsoredProduct();
        var entitlement = CreateEntitlement();
        var eligibility = ListingPromotionEligibility.Create(
            "berlin-recording-services",
            entitlement.ListingId,
            isPublished: true,
            isArchived: false,
            hasBlockingDispute: false,
            hasVerifiedContact: true,
            contactCapabilities: ["website"],
            categoryKeys: ["recording-studio"],
            districtKey: "mitte",
            sourceRevision: 1,
            Timestamp);
        return SponsoredPlacement.Create(
            placementId,
            revisionId,
            entitlement,
            product,
            eligibility,
            "berlin-recording-services",
            PlacementScopeType.Category,
            "recording-studio",
            locales,
            PromotionWindow.Create(startsAtUtc, endsAtUtc),
            priorityBand: 100,
            capacitySlot,
            presentationLabelKey: "sponsored",
            Guid.Parse("0198b100-0000-7000-8000-000000000103"),
            "placement contract",
            Timestamp,
            Digest('e'));
    }

    private static string Digest(char value) => new(value, 64);
}
