using Aggregator.Query.Application;
using Aggregator.Query.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aggregator.Query.Api;

[ApiController]
[Route("api/catalog-query/catalogs/{catalogKey}/sitemap-records")]
public sealed class CatalogSitemapController : ControllerBase
{
    private readonly ReadPublicSitemapService service;

    [ActivatorUtilitiesConstructor]
    public CatalogSitemapController(NpgsqlDataSource dataSource)
        : this(QuerySitemapApiComposition.Create(dataSource))
    {
    }

    public CatalogSitemapController(ReadPublicSitemapService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<PublicSitemapPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<PublicSitemapPageDto>> ReadAsync(
        [FromRoute] string catalogKey,
        [FromQuery] string? locale = null,
        [FromQuery] int pageSize = 1000,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var result = await service.ReadAsync(
            catalogKey,
            locale,
            pageSize,
            cursor,
            cancellationToken);
        if (result.Status == PublicSitemapReadStatus.ProjectionUnavailable)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "Query sitemap projection is unavailable.",
                    Detail = "No ready sitemap revision is active for the requested Catalog.",
                    Extensions =
                    {
                        ["owner"] = "Query.Sitemap",
                        ["code"] = "QUERY_SITEMAP_PROJECTION_UNAVAILABLE",
                        ["requiredAction"] = "Build and activate the exact Query sitemap projection before retrying.",
                    },
                });
        }

        return Ok(result.Page ?? throw new InvalidOperationException(
            "Ready Query sitemap result is missing its page contract."));
    }
}
