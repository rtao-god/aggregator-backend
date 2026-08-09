using Xunit;

namespace Architecture.Tests;

public sealed class IngestionCatalogProjectionPersistenceOwnershipTests
{
    [Fact]
    public void EventBackedProjectionHasNoParallelEfPersistenceModel()
    {
        var repository = RepositoryModel.Load();
        var dbContext = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/IngestionDbContext.cs");
        var rows = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/IngestionPersistenceRows.cs");
        var referenceReaders = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/EfIngestionReferenceReaders.cs");
        var canonicalReader = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/PostgresCatalogIngestionReferenceReader.cs");
        var canonicalStore = Read(
            repository,
            "src/Ingestion/Ingestion.Infrastructure/PostgresCatalogConfigurationProjectionStore.cs");

        Assert.DoesNotContain("CatalogReferences", dbContext, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureCatalogReference", dbContext, StringComparison.Ordinal);
        Assert.DoesNotContain("CatalogIngestionReferenceRow", rows, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ICatalogIngestionReferenceReader",
            referenceReaders,
            StringComparison.Ordinal);
        Assert.Contains(
            ": ICatalogIngestionReferenceReader",
            canonicalReader,
            StringComparison.Ordinal);
        Assert.Contains(
            ": ICatalogConfigurationProjectionStore",
            canonicalStore,
            StringComparison.Ordinal);
    }

    private static string Read(RepositoryModel repository, string relativePath) =>
        File.ReadAllText(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
