using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class PublicQueryServiceTests
{
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
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["recording-studio"] = 2,
                }),
        };
        var service = new PublicQueryService(store);

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
        Assert.NotNull(result.NextCursor);
        Assert.Equal(revision.Id, result.Metadata.PublicReadRevisionId);
        Assert.Equal(2, store.LastMaximumDocuments);
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
                new Dictionary<string, int>()),
        };
        var firstService = new PublicQueryService(firstStore);
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
                new Dictionary<string, int>()),
        };
        var secondService = new PublicQueryService(secondStore);

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
            [],
            [],
            new string('a', 64),
            revision.CreatedAtUtc);
        var store = new StubPublicQueryStore
        {
            Document = new PublicReadDocumentSnapshot(revision, LocalePolicy(), document),
        };
        var service = new PublicQueryService(store);

        var result = await service.GetByRouteAsync(
            "berlin-recording-services",
            "/en-GB/listings/studio",
            "en-GB",
            CancellationToken.None);

        Assert.Equal("exact", result.Listing.TranslationState);
        Assert.Equal("Studio EN", result.Listing.Title);
        Assert.Equal("Description", result.Listing.Description);
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
                new Dictionary<string, int>()),
        };
        var service = new PublicQueryService(store);

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

    private sealed class StubPublicQueryStore : IPublicQueryStore
    {
        public PublicReadPageSnapshot? Page { get; init; }

        public PublicReadDocumentSnapshot? Document { get; init; }

        public int LastMaximumDocuments { get; private set; }

        public Task<PublicReadPageSnapshot?> ReadPageAsync(
            string catalogKey,
            Guid? afterListingId,
            int maximumDocuments,
            string? categoryKey,
            CancellationToken cancellationToken)
        {
            LastMaximumDocuments = maximumDocuments;
            return Task.FromResult(Page);
        }

        public Task<PublicReadDocumentSnapshot?> ReadByRouteAsync(
            string catalogKey,
            string routePath,
            CancellationToken cancellationToken) => Task.FromResult(Document);
    }
}
