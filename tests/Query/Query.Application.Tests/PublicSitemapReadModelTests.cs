using Aggregator.Query.Application;
using Aggregator.Query.Contracts;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class PublicSitemapReadModelTests
{
    private static readonly Guid PublicReadRevisionId =
        Guid.Parse("01990f00-0000-7000-8000-000000000001");
    private static readonly Guid AnotherPublicReadRevisionId =
        Guid.Parse("01990f00-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadySliceMapsExactRevisionAndTypedRoutes()
    {
        var next = CreateCursor(PublicReadRevisionId, "/de-DE/studios/exact-studio");
        var store = new CapturingStore(new PublicSitemapSlice(
            PublicReadRevisionId,
            [CreateDocument()],
            next));
        var service = new ReadPublicSitemapService(store);

        var result = await service.ReadAsync(
            "recording-services",
            "de-DE",
            pageSize: 100,
            cursor: null,
            CancellationToken.None);

        Assert.Equal(PublicSitemapReadStatus.Ready, result.Status);
        var page = Assert.IsType<PublicSitemapPageDto>(result.Page);
        Assert.Equal(PublicReadRevisionId, page.PublicReadRevisionId);
        var item = Assert.Single(page.Items);
        Assert.Equal(PublicSeoRouteKindContract.Listing, item.RouteKind);
        Assert.Equal("recording-services", item.CatalogKey);
        Assert.Equal("de-DE", item.Locale);
        Assert.Equal("/de-DE/studios/exact-studio", item.Path);
        Assert.Equal(item.Path, item.CanonicalPath);
        Assert.NotNull(page.NextCursor);
        Assert.Equal(PublicReadRevisionId, PublicSitemapCursorCodec.Decode(page.NextCursor!).PublicReadRevisionId);

        Assert.NotNull(store.Request);
        Assert.Equal("recording-services", store.Request!.CatalogKey.Value);
        Assert.Equal("de-DE", store.Request.Locale!.Value);
        Assert.Equal(100, store.Request.PageSize);
    }

    [Fact]
    public async Task MissingActiveProjectionIsExplicitlyUnavailable()
    {
        var service = new ReadPublicSitemapService(new CapturingStore(result: null));

        var result = await service.ReadAsync(
            "recording-services",
            locale: null,
            pageSize: 100,
            cursor: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(PublicSitemapReadStatus.ProjectionUnavailable, result.Status);
        Assert.Null(result.Page);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task PageSizeOutsideContractIsRejected(int pageSize)
    {
        var service = new ReadPublicSitemapService(new CapturingStore(result: null));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReadAsync(
            "recording-services",
            locale: null,
            pageSize: pageSize,
            cursor: null,
            cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task InvalidLocaleIsRejectedBeforeStoreAccess()
    {
        var store = new CapturingStore(result: null);
        var service = new ReadPublicSitemapService(store);

        var exception = await Assert.ThrowsAsync<QueryDomainException>(() => service.ReadAsync(
            "recording-services",
            "de-de",
            pageSize: 100,
            cursor: null,
            cancellationToken: CancellationToken.None));

        Assert.Equal("QUERY_SEO_LOCALE_INVALID", exception.Code);
        Assert.Null(store.Request);
    }

    [Fact]
    public async Task CursorCannotContinueAfterActiveRevisionChanges()
    {
        var store = new CapturingStore(new PublicSitemapSlice(
            AnotherPublicReadRevisionId,
            [CreateDocument()],
            NextCursor: null));
        var service = new ReadPublicSitemapService(store);
        var encoded = PublicSitemapCursorCodec.Encode(
            CreateCursor(PublicReadRevisionId, "/de-DE/studios/exact-studio"));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ReadAsync(
            "recording-services",
            "de-DE",
            pageSize: 100,
            cursor: encoded,
            cancellationToken: CancellationToken.None));

        Assert.Equal("cursor", exception.ParamName);
    }

    [Fact]
    public async Task StoreCannotReturnMoreThanRequestedPageSize()
    {
        var store = new CapturingStore(new PublicSitemapSlice(
            PublicReadRevisionId,
            [CreateDocument(), CreateDocument()],
            NextCursor: null));
        var service = new ReadPublicSitemapService(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadAsync(
            "recording-services",
            "de-DE",
            pageSize: 1,
            cursor: null,
            cancellationToken: CancellationToken.None));
    }

    private static PublicSitemapCursor CreateCursor(Guid revisionId, string path) =>
        new(
            revisionId,
            QuerySeoCatalogKey.Create("recording-services"),
            QuerySeoLocale.Create("de-DE"),
            QuerySeoLocale.Create("de-DE"),
            QuerySeoPath.CreateIndexable(path));

    private static QuerySitemapDocument CreateDocument() =>
        QuerySitemapDocument.CreateIndexable(
            QuerySeoRouteKind.Listing,
            "recording-services",
            "de-DE",
            "/de-DE/studios/exact-studio",
            "/de-DE/studios/exact-studio",
            [
                QueryHreflangRoute.Create("de-DE", "/de-DE/studios/exact-studio"),
                QueryHreflangRoute.Create("en-GB", "/en-GB/studios/exact-studio"),
            ],
            Timestamp,
            isDraft: false,
            redirectsToAnotherRoute: false,
            isSuppressed: false);

    private sealed class CapturingStore(PublicSitemapSlice? result) : IPublicSitemapStore
    {
        public PublicSitemapPageRequest? Request { get; private set; }

        public Task<PublicSitemapSlice?> ReadPageAsync(
            PublicSitemapPageRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(result);
        }
    }
}
