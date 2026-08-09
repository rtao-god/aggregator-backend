using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;

namespace Analytics.Application.Tests;

public sealed class AnalyticsProjectionContractTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 4, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PublicReadProjectionCanonicalizesMembershipOrder()
    {
        var firstListingId = Guid.Parse("0198a300-0000-7000-8000-000000000001");
        var secondListingId = Guid.Parse("0198a300-0000-7000-8000-000000000002");

        var projection = CreatePublicReadProjection([secondListingId, firstListingId]);

        Assert.Equal([firstListingId, secondListingId], projection.PublicListingIds);
    }

    [Fact]
    public void DuplicatePublicListingMembershipIsRejected()
    {
        var listingId = Guid.Parse("0198a300-0000-7000-8000-000000000003");

        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            CreatePublicReadProjection([listingId, listingId]));

        Assert.Equal("ANALYTICS_PUBLIC_LISTING_DUPLICATE", exception.Code);
    }

    [Fact]
    public void EmptyPublicListingIdentityIsRejected()
    {
        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            CreatePublicReadProjection([Guid.Empty]));

        Assert.Equal("ANALYTICS_PUBLIC_LISTING_ID_INVALID", exception.Code);
    }

    [Fact]
    public void ListingAccessProjectionRequiresPositiveSourceRevision()
    {
        var exception = Assert.Throws<AnalyticsDomainException>(() =>
            ListingMetricsAccessProjection.Create(
                Guid.Parse("0198a300-0000-7000-8000-000000000004"),
                Guid.Parse("0198a300-0000-7000-8000-000000000005"),
                canViewAnalytics: true,
                sourceAggregateRevision: 0,
                new string('c', 64),
                Timestamp));

        Assert.Equal("ANALYTICS_ACCESS_REVISION_INVALID", exception.Code);
    }

    private static PublicReadReferenceProjection CreatePublicReadProjection(
        IEnumerable<Guid> listingIds) =>
        PublicReadReferenceProjection.Create(
            Guid.Parse("0198a300-0000-7000-8000-000000000010"),
            "berlin-recording-services",
            1,
            Guid.Parse("0198a300-0000-7000-8000-000000000011"),
            Guid.Parse("0198a300-0000-7000-8000-000000000012"),
            Guid.Parse("0198a300-0000-7000-8000-000000000013"),
            Guid.Parse("0198a300-0000-7000-8000-000000000014"),
            new string('a', 64),
            new string('b', 64),
            Timestamp,
            listingIds);
}
