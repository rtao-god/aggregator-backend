using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class PublicQueryServiceTests
{
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
            Page = new PublicReadPageSnapshot(
                revision,
                LocalePolicy(),
                [first, second],
                [],
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["recording-studio"] = 2,
                }),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));

        var result = await service.SearchAsync(
            "berlin-recording-services",
            "en-GB",
            "recording-studio",
            1,
            null,
            CancellationToken.None);

        var listing = Assert.Single(result.Organic);
        Assert.Equal("fallback", listing.TranslationState);
        Assert.Equal("de-DE", listing.ResolvedLocale);
        Assert.Empty(result.Sponsored);
        Assert.NotNull(result.NextCursor);
        Assert.Equal(revision.Id, result.Metadata.PublicReadRevisionId);
        Assert.Equal(2, store.LastMaximumDocuments);
        Assert.Equal("en-GB", store.LastRequestedLocale);
        Assert.Equal(Now, store.LastReadAtUtc);
    }

    [Fact]
    public async Task SearchReturnsSponsoredAndOrganicFromSamePublicReadRevision()
    {
        var revision = CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000002"));
        var listing = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000012"),
            "de-DE",
            "Sponsored Studio");
        var placement = CreatePlacement(listing.ListingId);
        var store = new StubPublicQueryStore
        {
            Page = new PublicReadPageSnapshot(
                revision,
                LocalePolicy(),
                [listing],
                [new PublicSponsoredListingSnapshot(placement, listing)],
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["recording-studio"] = 1,
                }),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));

        var result = await service.SearchAsync(
            "berlin-recording-services",
            "de-DE",
            null,
            20,
            null,
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
    public async Task ExpiredSponsoredPlacementIsRejectedAsStoreContractFailure()
    {
        var listing = CreateDocument(
            Guid.Parse("0198a300-0000-7000-8000-000000000013"),
            "de-DE",
            "Expired Studio");
        var expired = CreatePlacement(
            listing.ListingId,
            hardExpiryAtUtc: Now.AddMinutes(-1));
        var store = new StubPublicQueryStore
        {
            Page = new PublicReadPageSnapshot(
                CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000003")),
                LocalePolicy(),
                [],
                [new PublicSponsoredListingSnapshot(expired, listing)],
                new Dictionary<string, int>(StringComparer.Ordinal)),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => service.SearchAsync(
            "berlin-recording-services",
            "de-DE",
            null,
            20,
            null,
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
            Page = new PublicReadPageSnapshot(
                firstRevision,
                LocalePolicy(),
                [
                    document,
                    CreateDocument(
                        Guid.Parse("0198a300-0000-7000-8000-000000000022"),
                        "de-DE",
                        "Studio Zwei"),
                ],
                [],
                new Dictionary<string, int>()),
        };
        var firstService = new PublicQueryService(firstStore, new FixedClock(Now));
        var firstPage = await firstService.SearchAsync(
            "berlin-recording-services",
            "de-DE",
            null,
            1,
            null,
            CancellationToken.None);
        Assert.NotNull(firstPage.NextCursor);

        var secondStore = new StubPublicQueryStore
        {
            Page = new PublicReadPageSnapshot(
                CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000023")),
                LocalePolicy(),
                [],
                [],
                new Dictionary<string, int>()),
        };
        var secondService = new PublicQueryService(secondStore, new FixedClock(Now));

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => secondService.SearchAsync(
            "berlin-recording-services",
            "de-DE",
            null,
            1,
            firstPage.NextCursor,
            CancellationToken.None));

        Assert.Equal("QUERY_CURSOR_REVISION_MISMATCH", exception.Code);
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
            "berlin-recording-services",
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
            Page = new PublicReadPageSnapshot(
                CreateRevision(Guid.Parse("0198a300-0000-7000-8000-000000000040")),
                LocalePolicy(),
                [],
                [],
                new Dictionary<string, int>()),
        };
        var service = new PublicQueryService(store, new FixedClock(Now));

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => service.SearchAsync(
            "berlin-recording-services",
            "fr-FR",
            null,
            20,
            null,
            CancellationToken.None));

        Assert.Equal("QUERY_LOCALE_UNSUPPORTED", exception.Code);
    }

    private static QueryLocalePolicy LocalePolicy() =>
        QueryLocalePolicy.Create("de-DE", ["de-DE", "en-GB"]);

    private static PublicReadRevision CreateRevision(Guid id) =>
        PublicReadRevision.Restore(
            id,
            "berlin-recording-services",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero),
            new string('f', 64));

    private static QueryListingDocument CreateDocument(Guid listingId, string locale, string title) =>
        QueryListingDocument.Create(
            listingId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            QueryListingKind.Place,
            [new QueryLocalizedDocument(locale, $"/{locale}/listings/{listingId:N}", title, QueryFieldState.Missing, null)],
            ["recording-studio"],
            [],
            new QueryGeographyDocument(QueryGeographyState.PrimaryMarket, 52.5m, 13.4m, "mitte"),
            [],
            [],
            new string('a', 64),
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero));

    private static QueryPromotionPlacement CreatePlacement(
        Guid listingId,
        DateTimeOffset? hardExpiryAtUtc = null) =>
        QueryPromotionPlacement.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            listingId,
            "berlin-recording-services",
            "featured-listing",
            QueryPromotionPlacementScope.Catalog,
            "berlin-recording-services",
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

        public string? LastRequestedLocale { get; private set; }

        public DateTimeOffset? LastReadAtUtc { get; private set; }

        public Task<PublicReadPageSnapshot?> ReadPageAsync(
            string catalogKey,
            Guid? afterListingId,
            int maximumDocuments,
            string? categoryKey,
            string requestedLocale,
            DateTimeOffset readAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastMaximumDocuments = maximumDocuments;
            LastRequestedLocale = requestedLocale;
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
