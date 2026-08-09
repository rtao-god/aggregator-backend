using Aggregator.Query.Application;
using Aggregator.Query.Contracts;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class PublicReadActivationEventFactoryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 9, 8, 30, 0, TimeSpan.Zero);

    private static readonly Guid ListingA =
        Guid.Parse("0198a222-0000-7000-8000-000000000001");

    private static readonly Guid ListingB =
        Guid.Parse("0198a222-0000-7000-8000-000000000002");

    [Fact]
    public void MembershipDigestDoesNotDependOnInputOrder()
    {
        var revision = CreateRevision();
        var placementA = CreatePlacement(
            Guid.Parse("0198a222-0000-7000-8000-000000000010"),
            ListingA);
        var placementB = CreatePlacement(
            Guid.Parse("0198a222-0000-7000-8000-000000000011"),
            ListingB);

        var first = PublicReadActivationEventFactory.Create(
            Guid.Parse("0198a222-0000-7000-8000-000000000020"),
            revision,
            7,
            [ListingB, ListingA],
            [placementB, placementA],
            Now);
        var second = PublicReadActivationEventFactory.Create(
            Guid.Parse("0198a222-0000-7000-8000-000000000021"),
            revision,
            7,
            [ListingA, ListingB],
            [placementA, placementB],
            Now);

        Assert.Equal([ListingA, ListingB], first.PublicListingIds);
        Assert.Equal(
            [placementA.PlacementId, placementB.PlacementId],
            first.SponsoredPlacements.Select(item => item.PlacementId));
        Assert.Equal(first.MembershipDigest, second.MembershipDigest);
        Assert.Equal(64, first.MembershipDigest.Length);
        Assert.Equal(revision.Id, first.PublicReadRevisionId);
        Assert.Equal(revision.ContentDigest, first.PublicReadContentDigest);
    }

    [Fact]
    public void SponsoredPlacementOutsidePublicMembershipIsRejected()
    {
        var outsideListing =
            Guid.Parse("0198a222-0000-7000-8000-000000000099");

        var exception = Assert.Throws<QueryProjectionException>(() =>
            PublicReadActivationEventFactory.Create(
                Guid.Parse("0198a222-0000-7000-8000-000000000022"),
                CreateRevision(),
                8,
                [ListingA],
                [CreatePlacement(
                    Guid.Parse("0198a222-0000-7000-8000-000000000012"),
                    outsideListing)],
                Now));

        Assert.Equal(
            "QUERY_PUBLIC_READ_PLACEMENT_LISTING_NOT_PUBLIC",
            exception.Code);
        Assert.Equal("Query.PublicReadActivation", exception.Owner);
    }

    private static PublicReadRevision CreateRevision() =>
        PublicReadRevision.Restore(
            Guid.Parse("0198a222-0000-7000-8000-000000000030"),
            "catalog",
            Guid.Parse("0198a222-0000-7000-8000-000000000031"),
            Guid.Parse("0198a222-0000-7000-8000-000000000032"),
            Guid.Parse("0198a222-0000-7000-8000-000000000033"),
            Guid.Parse("0198a222-0000-7000-8000-000000000034"),
            Now,
            new string('a', 64));

    private static PublicReadSponsoredPlacementReference CreatePlacement(
        Guid placementId,
        Guid listingId) =>
        new(
            placementId,
            listingId,
            PublicReadPlacementScopeTypeContract.Catalog,
            "catalog",
            Now,
            Now.AddDays(1));
}
