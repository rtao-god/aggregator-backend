namespace Architecture.Tests;

public sealed class QueryFacetCatalogReachabilityTests
{
    [Fact]
    public void QueryOwnsTypedFacetCatalogContractAndApplicationBoundary()
    {
        var contracts = Read(
            "src/Query/Query.Contracts/PublicFacetCatalogContracts.cs");
        var application = Read(
            "src/Query/Query.Application/PublicFacetCatalog.cs");

        Assert.Contains(
            "PublicFacetCatalogResponse",
            contracts,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublicMarketZoneFacetValue",
            contracts,
            StringComparison.Ordinal);
        Assert.Contains(
            "IPublicFacetCatalogStore",
            application,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublicFacetCatalogService",
            application,
            StringComparison.Ordinal);
        Assert.Contains(
            "QUERY_PROJECTION_UNAVAILABLE",
            application,
            StringComparison.Ordinal);
        Assert.Contains(
            "QUERY_STORE_CONTRACT_INVALID",
            application,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublicFacetEndpointIsReadOnlyAndDelegatesToApplicationOwner()
    {
        var controller = Read(
            "src/Query/Query.Api/CatalogFacetController.cs");

        Assert.Contains(
            "api/catalog-query/catalogs/{catalogKey}/facets",
            controller,
            StringComparison.Ordinal);
        Assert.Contains("[HttpGet", controller, StringComparison.Ordinal);
        Assert.Contains(
            "PublicFacetCatalogService",
            controller,
            StringComparison.Ordinal);
        Assert.Contains(
            "QUERY_FACET_PARAMETER_UNKNOWN",
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
    public void FacetCatalogIsReadThroughSafetyAwareQueryOwner()
    {
        var publicStore = Read(
            "src/Query/Query.Infrastructure/NpgsqlPublicQueryStore.Facets.cs");
        var safetyStore = Read(
            "src/Query/Query.Infrastructure/SafetyAwarePublicQueryStore.Facets.cs");
        var safetyOwner = Read(
            "src/Query/Query.Infrastructure/SafetyAwarePublicQueryStore.cs");

        Assert.Contains(
            "ReadFacetCatalogAsync",
            publicStore,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadMarketZoneFacetCountsAsync",
            publicStore,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadFacetCountsAsync",
            safetyStore,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadSafetyMarketZoneFacetCountsAsync",
            safetyStore,
            StringComparison.Ordinal);
        Assert.Contains(
            "LoadSafetyAsync",
            safetyStore,
            StringComparison.Ordinal);
        Assert.Contains(
            "IPublicQueryStore, IPublicFacetCatalogStore",
            safetyOwner,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", publicStore, StringComparison.Ordinal);
        Assert.DoesNotContain("CatalogDbContext", publicStore, StringComparison.Ordinal);
        Assert.DoesNotContain("PromotionDbContext", publicStore, StringComparison.Ordinal);
        Assert.DoesNotContain("AnalyticsDbContext", publicStore, StringComparison.Ordinal);
    }

    [Fact]
    public void FacetCatalogUsesCompleteRevisionAndSafetySuppression()
    {
        var publicStore = Read(
            "src/Query/Query.Infrastructure/NpgsqlPublicQueryStore.Facets.cs");
        var safetyStore = Read(
            "src/Query/Query.Infrastructure/SafetyAwarePublicQueryStore.Facets.cs");
        var pageSafetyStore = Read(
            "src/Query/Query.Infrastructure/SafetyAwarePublicQueryStore.Page.cs");

        Assert.Contains(
            "context.Revision.BaseProjectionId",
            publicStore,
            StringComparison.Ordinal);
        Assert.Contains(
            "FROM documents.listing_geography",
            publicStore,
            StringComparison.Ordinal);
        Assert.DoesNotContain("afterListingId", publicStore, StringComparison.Ordinal);
        Assert.DoesNotContain("maximumDocuments", publicStore, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT @", publicStore, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "item.target_kind = 'listing'",
            safetyStore,
            StringComparison.Ordinal);
        Assert.Contains(
            "item.target_kind = 'route'",
            safetyStore,
            StringComparison.Ordinal);
        Assert.Contains(
            "item.target_kind = 'contact'",
            pageSafetyStore,
            StringComparison.Ordinal);
        Assert.Contains(
            "EnsureCatalogNotBlockedAsync",
            safetyStore,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompositionRootRegistersOneFacetStoreAndService()
    {
        var composition = Read(
            "src/Query/Query.Infrastructure/QueryInfrastructureServiceCollectionExtensions.cs");

        const string storeRegistration =
            "services.AddSingleton<IPublicFacetCatalogStore>(serviceProvider =>";
        Assert.Contains(storeRegistration, composition, StringComparison.Ordinal);
        Assert.Contains(
            "serviceProvider.GetRequiredService<SafetyAwarePublicQueryStore>()",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "services.AddSingleton<PublicFacetCatalogService>();",
            composition,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(composition, storeRegistration));
        Assert.Equal(
            1,
            CountOccurrences(
                composition,
                "services.AddSingleton<PublicFacetCatalogService>();"));
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
