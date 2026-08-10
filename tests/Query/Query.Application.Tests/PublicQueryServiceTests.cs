using Aggregator.Query.Application;
using Aggregator.Query.Contracts;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class PublicQueryServiceTests
{
    private const string CatalogKey = "berlin-recording-services";

    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SearchReturnsExplicitDefaultLocaleFallbackAndRevisionBoundCursor()
    {
        var revision = CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000001"));
        var first = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000010"),
            "de-DE",
            "Studio Eins");
        var second = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000011"),
            "de-DE",
            "Studio Zwei");
        var store = new StubPublicQueryStore
        {
            Page = CreatePage(
                revision,
                [first, second],
                categoryFacets: new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["recording-studio"] = 2,
                }),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));

        var result = await service.SearchAsync(
            CatalogKey,
            SearchRequest(
                locale: "en-GB",
                categoryKey: "recording-studio",
                pageSize: 1),
            CancellationToken.None);

        var listing = Assert.Single(result.Organic);
        Assert.Equal("fallback", listing.TranslationState);
        Assert.Equal("de-DE", listing.ResolvedLocale);
        Assert.Empty(result.Sponsored);
        Assert.NotNull(result.NextCursor);
        Assert.Equal(revision.Id, result.Metadata.PublicReadRevisionId);
        Assert.Equal(2, store.LastMaximumDocuments);
        Assert.Equal("en-GB", store.LastCriteria?.RequestedLocale);
        Assert.Equal("recording-studio", store.LastCriteria?.CategoryKey);
        Assert.Equal(Now, store.LastReadAtUtc);
    }

    [Fact]
    public async Task TypedCriteriaAndCompleteProjectionFacetsAreReturnedExactly()
    {
        var listing = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000012"),
            "de-DE",
            "Typed Studio",
            districtKey: "mitte",
            listingKind: QueryListingKind.Place,
            contactKinds: [QueryContactKind.WhatsApp]);
        var store = new StubPublicQueryStore
        {
            Page = CreatePage(
                CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000002")),
                [listing],
                categoryFacets: new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["recording-studio"] = 7,
                    ["rehearsal-room"] = 3,
                },
                districtFacets: new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["mitte"] = 5,
                },
                listingKindFacets: new Dictionary<QueryListingKind, int>
                {
                    [QueryListingKind.Place] = 8,
                    [QueryListingKind.Provider] = 2,
                },
                contactKindFacets: new Dictionary<QueryContactKind, int>
                {
                    [QueryContactKind.WhatsApp] = 4,
                    [QueryContactKind.Website] = 9,
                }),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));

        var result = await service.SearchAsync(
            CatalogKey,
            SearchRequest(
                categoryKey: "recording-studio",
                districtKey: "mitte",
                listingKind: PublicListingKindContract.Place,
                contactKind: PublicContactKindContract.WhatsApp,
                marketZone: PublicMarketZoneContract.PrimaryMarket),
            CancellationToken.None);

        Assert.Equal("recording-studio", store.LastCriteria?.CategoryKey);
        Assert.Equal("mitte", store.LastCriteria?.DistrictKey);
        Assert.Equal(QueryListingKind.Place, store.LastCriteria?.ListingKind);
        Assert.Equal(QueryContactKind.WhatsApp, store.LastCriteria?.ContactKind);
        Assert.Equal(QueryGeographyState.PrimaryMarket, store.LastCriteria?.MarketZone);
        Assert.Equal("mitte", result.Query.DistrictKey);
        Assert.Equal(PublicListingKindContract.Place, result.Query.ListingKind);
        Assert.Equal(PublicContactKindContract.WhatsApp, result.Query.ContactKind);
        Assert.Equal(PublicMarketZoneContract.PrimaryMarket, result.Query.MarketZone);
        Assert.Equal(2, result.CategoryFacets.Count);
        Assert.Equal(7, result.CategoryFacets.Single(item => item.Key == "recording-studio").Count);
        Assert.Equal(5, Assert.Single(result.DistrictFacets).Count);
        Assert.Equal(8, result.ListingKindFacets.Single(item =>
            item.Value == PublicListingKindContract.Place).Count);
        Assert.Equal(4, result.ContactKindFacets.Single(item =>
            item.Value == PublicContactKindContract.WhatsApp).Count);
    }

    [Fact]
    public async Task SearchReturnsSponsoredAndOrganicFromSamePublicReadRevision()
    {
        var revision = CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000003"));
        var listing = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000013"),
            "de-DE",
            "Sponsored Studio");
        var placement = CreatePlacement(listing.ListingId);
        var store = new StubPublicQueryStore
        {
            Page = CreatePage(
                revision,
                [listing],
                [new PublicSponsoredListingSnapshot(placement, listing)],
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["recording-studio"] = 1,
                }),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));

        var result = await service.SearchAsync(
            CatalogKey,
            SearchRequest(),
            CancellationToken.None);

        var sponsored = Assert.Single(result.Sponsored);
        var organic = Assert.Single(result.Organic);
        Assert.Equal(placement.PlacementId, sponsored.PlacementId);
        Assert.Equal(placement.HardExpiryAtUtc, sponsored.HardExpiryAtUtc);
        Assert.Equal("sponsored", sponsored.DisclosureLabelKey);
        Assert.Equal(listing.ListingId, sponsored.Listing.ListingId);
        Assert.Equal(listing.ListingId, organic.ListingId);
        Assert.Equal(revision.Id, result.Metadata.PublicReadRevisionId);
        Assert.Equal(revision.PromotionOverlayId, result.Metadata.PromotionOverlayId);
    }

    [Fact]
    public async Task DistrictSponsoredPlacementRequiresMatchingDistrictCriteria()
    {
        var listing = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000014"),
            "de-DE",
            "Mitte Studio",
            districtKey: "mitte");
        var placement = CreatePlacement(
            listing.ListingId,
            scope: QueryPromotionPlacementScope.District,
            scopeKey: "mitte");
        var store = new StubPublicQueryStore
        {
            Page = CreatePage(
                CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000004")),
                [listing],
                [new PublicSponsoredListingSnapshot(placement, listing)]),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));

        var result = await service.SearchAsync(
            CatalogKey,
            SearchRequest(districtKey: "mitte"),
            CancellationToken.None);

        Assert.Equal("district", Assert.Single(result.Sponsored).ScopeType);
        Assert.Equal("mitte", Assert.Single(result.Sponsored).ScopeKey);
    }

    [Fact]
    public async Task DocumentOutsideRequestedContactFilterIsStoreContractFailure()
    {
        var listing = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000015"),
            "de-DE",
            "Website Only",
            contactKinds: [QueryContactKind.Website]);
        var store = new StubPublicQueryStore
        {
            Page = CreatePage(
                CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000005")),
                [listing]),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => service.SearchAsync(
            CatalogKey,
            SearchRequest(contactKind: PublicContactKindContract.WhatsApp),
            CancellationToken.None));

        Assert.Equal("QUERY_STORE_CONTRACT_INVALID", exception.Code);
    }

    [Fact]
    public async Task DocumentOutsideRequestedMarketZoneIsStoreContractFailure()
    {
        var listing = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000016"),
            "de-DE",
            "Nearby Studio",
            geographyState: QueryGeographyState.NearbyMarket);
        var store = new StubPublicQueryStore
        {
            Page = CreatePage(
                CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000006")),
                [listing]),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => service.SearchAsync(
            CatalogKey,
            SearchRequest(marketZone: PublicMarketZoneContract.PrimaryMarket),
            CancellationToken.None));

        Assert.Equal("QUERY_STORE_CONTRACT_INVALID", exception.Code);
    }

    [Fact]
    public async Task ExpiredSponsoredPlacementIsRejectedAsStoreContractFailure()
    {
        var listing = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000016"),
            "de-DE",
            "Expired Studio");
        var expired = CreatePlacement(
            listing.ListingId,
            hardExpiryAtUtc: Now.AddMinutes(-1));
        var store = new StubPublicQueryStore
        {
            Page = CreatePage(
                CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000006")),
                [],
                [new PublicSponsoredListingSnapshot(expired, listing)]),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => service.SearchAsync(
            CatalogKey,
            SearchRequest(),
            CancellationToken.None));

        Assert.Equal("QUERY_STORE_CONTRACT_INVALID", exception.Code);
    }

    [Fact]
    public async Task CursorFromPreviousPublicReadRevisionIsRejected()
    {
        var firstRevision = CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000020"));
        var document = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000021"),
            "de-DE",
            "Studio");
        var firstStore = new StubPublicQueryStore
        {
            Page = CreatePage(
                firstRevision,
                [
                    document,
                    CreateDocument(
                        Guid.Parse("0198a300-0000-7000-8000-000000000022"),
                        "de-DE",
                        "Studio Zwei"),
                ]),
        };
        var firstService = new PublicQueryService(firstStore, new FixedClock(Now));
        var firstPage = await firstService.SearchAsync(
            CatalogKey,
            SearchRequest(pageSize: 1),
            CancellationToken.None);
        Assert.NotNull(firstPage.NextCursor);

        var secondStore = new StubPublicQueryStore
        {
            Page = CreatePage(
                CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000023")),
                []),
        };
        var secondService = new PublicQueryService(secondStore, new FixedClock(Now));

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => secondService.SearchAsync(
            CatalogKey,
            SearchRequest(pageSize: 1, cursor: firstPage.NextCursor),
            CancellationToken.None));

        Assert.Equal("QUERY_CURSOR_REVISION_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task CursorCannotBeReusedAfterAnyFilterChanges()
    {
        var revision = CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000024"));
        var first = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000025"),
            "de-DE",
            "Mitte One",
            districtKey: "mitte",
            contactKinds: [QueryContactKind.Website]);
        var second = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000026"),
            "de-DE",
            "Mitte Two",
            districtKey: "mitte",
            contactKinds: [QueryContactKind.Website]);
        var store = new StubPublicQueryStore
        {
            Page = CreatePage(revision, [first, second]),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));
        var firstPage = await service.SearchAsync(
            CatalogKey,
            SearchRequest(
                districtKey: "mitte",
                contactKind: PublicContactKindContract.Website,
                pageSize: 1),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => service.SearchAsync(
            CatalogKey,
            SearchRequest(
                districtKey: "mitte",
                contactKind: PublicContactKindContract.Phone,
                pageSize: 1,
                cursor: firstPage.NextCursor),
            CancellationToken.None));

        Assert.Equal("QUERY_CURSOR_SCOPE_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task CursorCannotBeReusedAfterMarketZoneChanges()
    {
        var revision = CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000027"));
        var first = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000028"),
            "de-DE",
            "Primary One");
        var second = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000029"),
            "de-DE",
            "Primary Two");
        var store = new StubPublicQueryStore
        {
            Page = CreatePage(revision, [first, second]),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));
        var firstPage = await service.SearchAsync(
            CatalogKey,
            SearchRequest(
                pageSize: 1,
                marketZone: PublicMarketZoneContract.PrimaryMarket),
            CancellationToken.None);
        Assert.NotNull(firstPage.NextCursor);

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => service.SearchAsync(
            CatalogKey,
            SearchRequest(
                pageSize: 1,
                cursor: firstPage.NextCursor,
                marketZone: PublicMarketZoneContract.NearbyMarket),
            CancellationToken.None));

        Assert.Equal("QUERY_CURSOR_SCOPE_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task CardUsesExactRequestedLocalizationWhenAvailable()
    {
        var revision = CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000030"));
        var contactId = Guid.Parse("0198a300-0000-7000-8000-000000000039");
        var document = QueryListingDocument.Create(
            Guid.Parse("0198a300-0000-7000-8000-000000000031"),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            QueryListingKind.Place,
            [
                new QueryLocalizedDocument("de-DE", "/de-DE/listings/studio", "Studio", QueryFieldState.Missing, null),
                new QueryLocalizedDocument("en-GB", "/en-GB/listings/studio", "Studio EN", QueryFieldState.Observed, "Description"),
            ],
            ["recording-studio"],
            [],
            new QueryGeographyDocument(QueryGeographyState.PrimaryMarket, 52.5m, 13.4m, "mitte"),
            [
                new QueryContactDocument(
                    contactId,
                    QueryContactKind.Website,
                    "https://example.test",
                    null),
            ],
            [],
            new string('a', 64),
            revision.CreatedAtUtc);
        var store = new StubPublicQueryStore
        {
            Document = new PublicReadDocumentSnapshot(revision, LocalePolicy(), document),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));

        var result = await service.GetByRouteAsync(
            CatalogKey,
            "/en-GB/listings/studio",
            "en-GB",
            CancellationToken.None);

        Assert.Equal("exact", result.Listing.TranslationState);
        Assert.Equal("Studio EN", result.Listing.Title);
        Assert.Equal("Description", result.Listing.Description);
        Assert.Equal(contactId, Assert.Single(result.Contacts).ContactId);
    }

    [Fact]
    public async Task UnsupportedLocaleIsExplicitContractFailure()
    {
        var store = new StubPublicQueryStore
        {
            Page = CreatePage(
                CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000040")),
                []),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => service.SearchAsync(
            CatalogKey,
            SearchRequest(locale: "fr-FR"),
            CancellationToken.None));

        Assert.Equal("QUERY_LOCALE_UNSUPPORTED", exception.Code);
    }

    private static PublicListingSearchRequest SearchRequest(
        string locale = "de-DE",
        string? categoryKey = null,
        string? districtKey = null,
        PublicListingKindContract? listingKind = null,
        PublicContactKindContract? contactKind = null,
        int pageSize = 20,
        string? cursor = null,
        PublicMarketZoneContract? marketZone = null) =>
        new(
            locale,
            categoryKey,
            districtKey,
            listingKind,
            contactKind,
            pageSize,
            cursor,
            marketZone);

    private static PublicReadPageSnapshot CreatePage(
        PublicReadRevision revision,
        IReadOnlyList<QueryListingDocument> documents,
        IReadOnlyList<PublicSponsoredListingSnapshot>? sponsored = null,
        IReadOnlyDictionary<string, int>? categoryFacets = null,
        IReadOnlyDictionary<string, int>? districtFacets = null,
        IReadOnlyDictionary<QueryListingKind, int>? listingKindFacets = null,
        IReadOnlyDictionary<QueryContactKind, int>? contactKindFacets = null) =>
        new(
            revision,
            LocalePolicy(),
            documents,
            sponsored ?? [],
            categoryFacets ?? new Dictionary<string, int>(StringComparer.Ordinal),
            districtFacets ?? new Dictionary<string, int>(StringComparer.Ordinal),
            listingKindFacets ?? new Dictionary<QueryListingKind, int>(),
            contactKindFacets ?? new Dictionary<QueryContactKind, int>());

    private static QueryLocalePolicy LocalePolicy() =>
        QueryLocalePolicy.Create("de-DE", ["de-DE", "en-GB"]);

    private static PublicReadRevision CreateRevision(Guid id) =>
        PublicReadRevision.Restore(
            id,
            CatalogKey,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero),
            new string('f', 64));

    private static QueryListingDocument CreateDocument(
        Guid listingId,
        string locale,
        string title,
        string districtKey = "mitte",
        QueryListingKind listingKind = QueryListingKind.Place,
        IReadOnlyList<QueryContactKind>? contactKinds = null,
        QueryGeographyState geographyState = QueryGeographyState.PrimaryMarket) =>
        QueryListingDocument.Create(
            listingId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            listingKind,
            [new QueryLocalizedDocument(locale, $"/{locale}/listings/{listingId:N}", title, QueryFieldState.Missing, null)],
            ["recording-studio"],
            [],
            new QueryGeographyDocument(geographyState, 52.5m, 13.4m, districtKey),
            (contactKinds ?? [])
                .Select((kind, index) => new QueryContactDocument(
                    Guid.CreateVersion7(),
                    kind,
                    kind switch
                    {
                        QueryContactKind.Website => $"https://example-{index}.test",
                        QueryContactKind.Email => $"studio-{index}@example.test",
                        QueryContactKind.Phone => $"+493000000{index}",
                        QueryContactKind.WhatsApp => $"https://wa.me/493000000{index}",
                        QueryContactKind.BookingReference => $"booking:{index}",
                        QueryContactKind.MapReference => $"map:{index}",
                        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                    },
                    null))
                .ToArray(),
            [],
            new string('a', 64),
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero));

    private static QueryPromotionPlacement CreatePlacement(
        Guid listingId,
        DateTimeOffset? hardExpiryAtUtc = null,
        QueryPromotionPlacementScope scope = QueryPromotionPlacementScope.Catalog,
        string? scopeKey = null) =>
        QueryPromotionPlacement.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            listingId,
            CatalogKey,
            "featured-listing",
            scope,
            scopeKey ?? CatalogKey,
            ["de-DE", "en-GB"],
            Now.AddHours(-1),
            Now.AddDays(1),
            hardExpiryAtUtc ?? Now.AddHours(1),
            10,
            1,
            "sponsored",
            QueryPromotionPlacementState.Active,
            3,
            Now.AddMinutes(-5));

    private sealed class FixedClock(DateTimeOffset value) : IQueryClock
    {
        public DateTimeOffset GetUtcNow() => value;
    }

    private sealed class StubPublicQueryStore : IPublicQueryStore
    {
        public PublicReadPageSnapshot? Page { get; init; }

        public PublicReadDocumentSnapshot? Document { get; init; }

        public int LastMaximumDocuments { get; private set; }

        public PublicListingSearchCriteria? LastCriteria { get; private set; }

        public DateTimeOffset? LastReadAtUtc { get; private set; }

        public Task<PublicReadPageSnapshot?> ReadPageAsync(
            string catalogKey,
            Guid? afterListingId,
            int maximumDocuments,
            PublicListingSearchCriteria criteria,
            DateTimeOffset readAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastMaximumDocuments = maximumDocuments;
            LastCriteria = criteria;
            LastReadAtUtc = readAtUtc;
            return Task.FromResult(Page);
        }

        public Task<PublicReadDocumentSnapshot?> ReadByRouteAsync(
            string catalogKey,
            string routePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Document);
        }
    }
}
