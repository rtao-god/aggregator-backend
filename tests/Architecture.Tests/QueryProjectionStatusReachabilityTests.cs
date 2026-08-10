namespace Architecture.Tests;

public sealed class QueryProjectionStatusReachabilityTests
{
    [Fact]
    public void QueryOwnsTypedProjectionStatusContractAndApplicationBoundary()
    {
        var contracts = Read(
            "src/Query/Query.Contracts/PublicProjectionStatusContracts.cs");
        var application = Read(
            "src/Query/Query.Application/PublicProjectionStatus.cs");

        Assert.Contains(
            "PublicCatalogProjectionStatusResponse",
            contracts,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublicProjectionStatusStateContract",
            contracts,
            StringComparison.Ordinal);
        Assert.Contains(
            "IPublicProjectionStatusStore",
            application,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadPublicProjectionStatusService",
            application,
            StringComparison.Ordinal);
        Assert.Contains(
            "QUERY_PROJECTION_STATUS_NOT_FOUND",
            application,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublicEndpointIsReadOnlyAndDelegatesToApplicationOwner()
    {
        var controller = Read(
            "src/Query/Query.Api/CatalogProjectionStatusController.cs");

        Assert.Contains(
            "api/catalog-query/catalogs/{catalogKey}/projection-status",
            controller,
            StringComparison.Ordinal);
        Assert.Contains("[HttpGet", controller, StringComparison.Ordinal);
        Assert.Contains(
            "ReadPublicProjectionStatusService",
            controller,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("DbContext", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO", controller, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", controller, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", controller, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Rebuild", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Repair", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceReadsOnlyQueryOwnedSchemas()
    {
        var store = Read(
            "src/Query/Query.Infrastructure/PostgresPublicProjectionStatusStore.cs");

        Assert.Contains("projection.current_public_read", store, StringComparison.Ordinal);
        Assert.Contains("projection.catalog_activation_checkpoint", store, StringComparison.Ordinal);
        Assert.Contains("projection.catalog_visibility_block", store, StringComparison.Ordinal);
        Assert.Contains("seo_projection.active_sitemap_revision", store, StringComparison.Ordinal);
        Assert.Contains("SELECT", store, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO", store, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", store, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", store, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CatalogDbContext", store, StringComparison.Ordinal);
        Assert.DoesNotContain("PromotionDbContext", store, StringComparison.Ordinal);
        Assert.DoesNotContain("AnalyticsDbContext", store, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", store, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositionRootRegistersOneStatusStoreAndService()
    {
        var composition = Read(
            "src/Query/Query.Infrastructure/QueryInfrastructureServiceCollectionExtensions.cs");

        Assert.Contains(
            "IPublicProjectionStatusStore, PostgresPublicProjectionStatusStore",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadPublicProjectionStatusService",
            composition,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(
                composition,
                "IPublicProjectionStatusStore, PostgresPublicProjectionStatusStore"));
    }

    [Fact]
    public void CatalogCheckpointIsBoundToBasePublicationNotCurrentOverlayRevision()
    {
        var application = Read(
            "src/Query/Query.Application/PublicProjectionStatus.cs");
        var store = Read(
            "src/Query/Query.Infrastructure/PostgresPublicProjectionStatusStore.cs");

        Assert.Contains("checkpoint.base_projection_id", store, StringComparison.Ordinal);
        Assert.Contains("checkpoint.source_publication_id", store, StringComparison.Ordinal);
        Assert.Contains(
            "checkpointBaseProjectionId != revision.BaseProjectionId",
            application,
            StringComparison.Ordinal);
        Assert.Contains(
            "checkpointSourcePublicationId != revision.SourcePublicationId",
            application,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "checkpointRevisionId != revision.Id",
            application,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }

        return count;
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
