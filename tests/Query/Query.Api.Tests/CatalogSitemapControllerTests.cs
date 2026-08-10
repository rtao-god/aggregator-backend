using Aggregator.Query.Api;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Query.Api.Tests;

public sealed class CatalogSitemapControllerTests
{
    private static readonly Guid PublicReadRevisionId =
        Guid.Parse("01990f30-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadyProjectionReturnsTypedPage()
    {
        var service = new ReadPublicSitemapService(new StubStore(new PublicSitemapSlice(
            PublicReadRevisionId,
            [CreateDocument()],
            NextCursor: null)));
        var controller = new CatalogSitemapController(service);

        var response = await controller.ReadAsync(
            "recording-services",
            "de-DE",
            pageSize: 100,
            cursor: null,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var page = Assert.IsType<Aggregator.Query.Contracts.PublicSitemapPageDto>(ok.Value);
        Assert.Equal(PublicReadRevisionId, page.PublicReadRevisionId);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task MissingProjectionReturnsExplicitServiceUnavailableProblem()
    {
        var controller = new CatalogSitemapController(
            new ReadPublicSitemapService(new StubStore(result: null)));

        var response = await controller.ReadAsync(
            "recording-services",
            locale: null,
            pageSize: 100,
            cursor: null,
            cancellationToken: CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(unavailable.Value);
        Assert.Equal(
            "QUERY_SITEMAP_PROJECTION_UNAVAILABLE",
            problem.Extensions["code"]);
    }

    private static QuerySitemapDocument CreateDocument() =>
        QuerySitemapDocument.CreateIndexable(
            QuerySeoRouteKind.Listing,
            "recording-services",
            "de-DE",
            "/de-DE/studios/exact-studio",
            "/de-DE/studios/exact-studio",
            [QueryHreflangRoute.Create("de-DE", "/de-DE/studios/exact-studio")],
            Timestamp,
            isDraft: false,
            redirectsToAnotherRoute: false,
            isSuppressed: false);

    private sealed class StubStore(PublicSitemapSlice? result) : IPublicSitemapStore
    {
        public Task<PublicSitemapSlice?> ReadPageAsync(
            PublicSitemapPageRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
