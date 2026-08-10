using Aggregator.Query.Application;
using Aggregator.Query.Contracts;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class PublicFacetCatalogServiceTests
{
    private const string CatalogKey = "berlin-recording-services";

    private static readonly DateTimeOffset Now =
        new(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompleteSafetyFilteredFacetCatalogIsMappedDeterministically()
    {
        var revision = CreateRevision();
        var store = new StubFacetStore
        {
            Snapshot = new PublicFacetCatalogSnapshot(
                revision,
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["rehearsal-room"] = 3,
                    ["recording-studio"] = 7,
                },
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["mitte"] = 5,
                },
                new Dictionary<QueryListingKind, int>
                {
                    [QueryListingKind.Provider] = 2,
                    [QueryListingKind.Place] = 8,
                },
                new Dictionary<QueryContactKind, int>
                {
                    [QueryContactKind.WhatsApp] = 4,
                    [QueryContactKind.Website] = 9,
                },
                new Dictionary<QueryGeographyState, int>
                {
                    [QueryGeographyState.NearbyMarket] = 3,
                    [QueryGeographyState.PrimaryMarket] = 7,
                }),
        };
        var service = new PublicFacetCatalogService(
            store,
            new FixedClock(Now));

        var result = await service.GetAsync(CatalogKey, CancellationToken.None);

        Assert.Equal(revision.Id, result.Metadata.PublicReadRevisionId);
        Assert.Equal(
            ["recording-studio", "rehearsal-room"],
            result.CategoryFacets.Select(item => item.Key));
        Assert.Equal(5, Assert.Single(result.DistrictFacets).Count);
        Assert.Equal(
            [PublicListingKindContract.Place, PublicListingKindContract.Provider],
            result.ListingKindFacets.Select(item => item.Value));
        Assert.Equal(
            [PublicContactKindContract.Website, PublicContactKindContract.WhatsApp],
            result.ContactKindFacets.Select(item => item.Value));
        Assert.Equal(
            [PublicMarketZoneContract.PrimaryMarket, PublicMarketZoneContract.NearbyMarket],
            result.MarketZoneFacets.Select(item => item.Value));
        Assert.Equal(CatalogKey, store.LastCatalogKey);
        Assert.Equal(Now, store.LastReadAtUtc);
    }

    [Fact]
    public async Task MissingActiveProjectionIsExplicitlyUnavailable()
    {
        var service = new PublicFacetCatalogService(
            new StubFacetStore(),
            new FixedClock(Now));

        var exception = await Assert.ThrowsAsync<QueryReadException>(() =>
            service.GetAsync(CatalogKey, CancellationToken.None));

        Assert.Equal("QUERY_PROJECTION_UNAVAILABLE", exception.Code);
        Assert.Equal(503, exception.StatusCode);
    }

    [Fact]
    public async Task InvalidFacetCountIsStoreContractFailure()
    {
        var store = new StubFacetStore
        {
            Snapshot = new PublicFacetCatalogSnapshot(
                CreateRevision(),
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["recording-studio"] = 0,
                },
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<QueryListingKind, int>(),
                new Dictionary<QueryContactKind, int>(),
                new Dictionary<QueryGeographyState, int>()),
        };
        var service = new PublicFacetCatalogService(
            store,
            new FixedClock(Now));

        var exception = await Assert.ThrowsAsync<QueryReadException>(() =>
            service.GetAsync(CatalogKey, CancellationToken.None));

        Assert.Equal("QUERY_STORE_CONTRACT_INVALID", exception.Code);
        Assert.Equal(500, exception.StatusCode);
    }

    [Fact]
    public async Task NonUtcClockIsRejectedBeforePersistence()
    {
        var store = new StubFacetStore();
        var service = new PublicFacetCatalogService(
            store,
            new FixedClock(Now.ToOffset(TimeSpan.FromHours(2))));

        var exception = await Assert.ThrowsAsync<QueryReadException>(() =>
            service.GetAsync(CatalogKey, CancellationToken.None));

        Assert.Equal("QUERY_STORE_CONTRACT_INVALID", exception.Code);
        Assert.Equal(0, store.ReadCount);
    }

    private static PublicReadRevision CreateRevision() =>
        PublicReadRevision.Restore(
            Guid.Parse("0198ff40-0000-7000-8000-000000000001"),
            CatalogKey,
            Guid.Parse("0198ff40-0000-7000-8000-000000000002"),
            Guid.Parse("0198ff40-0000-7000-8000-000000000003"),
            Guid.Parse("0198ff40-0000-7000-8000-000000000004"),
            Guid.Parse("0198ff40-0000-7000-8000-000000000005"),
            Now.AddMinutes(-5),
            new string('a', 64));

    private sealed class FixedClock(DateTimeOffset value) : IQueryClock
    {
        public DateTimeOffset GetUtcNow() => value;
    }

    private sealed class StubFacetStore : IPublicFacetCatalogStore
    {
        public PublicFacetCatalogSnapshot? Snapshot { get; init; }

        public int ReadCount { get; private set; }

        public string? LastCatalogKey { get; private set; }

        public DateTimeOffset? LastReadAtUtc { get; private set; }

        public Task<PublicFacetCatalogSnapshot?> ReadAsync(
            string catalogKey,
            DateTimeOffset readAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            LastCatalogKey = catalogKey;
            LastReadAtUtc = readAtUtc;
            return Task.FromResult(Snapshot);
        }
    }
}
