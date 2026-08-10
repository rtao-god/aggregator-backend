namespace Architecture.Tests;

public sealed class QuerySitemapReachabilityTests
{
    [Fact]
    public void QueryOwnsTypedSitemapContractDomainAndApplicationBoundaries()
    {
        var contracts = Read("src/Query/Query.Contracts/PublicSeoContracts.cs");
        var domain = Read("src/Query/Query.Domain/QuerySeoDocuments.cs");
        var application = Read("src/Query/Query.Application/PublicSitemapReadModel.cs");
        var projection = Read("src/Query/Query.Application/PublicSitemapProjection.cs");

        Assert.Contains("PublicSitemapPageDto", contracts, StringComparison.Ordinal);
        Assert.Contains("QuerySitemapDocument", domain, StringComparison.Ordinal);
        Assert.Contains("IPublicSitemapStore", application, StringComparison.Ordinal);
        Assert.Contains("ReadPublicSitemapService", application, StringComparison.Ordinal);
        Assert.Contains("IPublicSitemapProjectionStore", projection, StringComparison.Ordinal);
        Assert.Contains("BuildPublicSitemapProjectionService", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicEndpointUsesReadOwnerAndNeverProjectionWriter()
    {
        var controller = Read("src/Query/Query.Api/CatalogSitemapController.cs");
        var composition = Read("src/Query/Query.Api/QuerySitemapApiComposition.cs");

        Assert.Contains(
            "api/catalog-query/catalogs/{catalogKey}/sitemap-records",
            controller,
            StringComparison.Ordinal);
        Assert.Contains("ReadPublicSitemapService", controller, StringComparison.Ordinal);
        Assert.Contains("QuerySitemapApiComposition.Create", controller, StringComparison.Ordinal);
        Assert.Contains("PostgresPublicSitemapStore", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildPublicSitemapProjectionService", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("PostgresPublicSitemapProjectionStore", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO", controller, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", controller, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", controller, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QuerySitemapPersistenceStaysInsideQueryInfrastructure()
    {
        var readStore = Read(
            "src/Query/Query.Infrastructure/PostgresPublicSitemapStore.cs");
        var projectionStore = Read(
            "src/Query/Query.Infrastructure/PostgresPublicSitemapProjectionStore.cs");

        Assert.Contains("namespace Aggregator.Query.Infrastructure", readStore, StringComparison.Ordinal);
        Assert.Contains("namespace Aggregator.Query.Infrastructure", projectionStore, StringComparison.Ordinal);
        Assert.Contains("seo_projection.", readStore, StringComparison.Ordinal);
        Assert.Contains("seo_projection.", projectionStore, StringComparison.Ordinal);
        Assert.DoesNotContain("CatalogDbContext", readStore, StringComparison.Ordinal);
        Assert.DoesNotContain("PromotionDbContext", readStore, StringComparison.Ordinal);
        Assert.DoesNotContain("AnalyticsDbContext", readStore, StringComparison.Ordinal);
        Assert.DoesNotContain("CatalogDbContext", projectionStore, StringComparison.Ordinal);
        Assert.DoesNotContain("PromotionDbContext", projectionStore, StringComparison.Ordinal);
        Assert.DoesNotContain("AnalyticsDbContext", projectionStore, StringComparison.Ordinal);
    }

    [Fact]
    public void SitemapProjectionIsRevisionBoundAndCannotUseLatestDiscovery()
    {
        var migration = Read(
            "src/Query/Query.Migrations/Migrations/V015__query_sitemap_revision_pointer.sql");
        var cursor = Read(
            "src/Query/Query.Application/PublicSitemapCursorCodec.cs");

        Assert.Contains("public_read_revision_id", migration, StringComparison.Ordinal);
        Assert.Contains("active_sitemap_revision", migration, StringComparison.Ordinal);
        Assert.Contains("PublicReadRevisionId", cursor, StringComparison.Ordinal);
        Assert.Contains("EnsureScope", cursor, StringComparison.Ordinal);
        Assert.DoesNotContain("latest", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("latest", cursor, StringComparison.OrdinalIgnoreCase);
    }

    private static string Read(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(
            directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Repository file '{relativePath}' was not found.");
        return File.ReadAllText(path);
    }
}
