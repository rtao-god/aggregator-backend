using Aggregator.CatalogMedia.Application;
using Aggregator.CatalogMedia.Infrastructure;

namespace Catalog.Media.Infrastructure.Tests;

public sealed class CatalogMediaInfrastructureBoundaryTests
{
    [Fact]
    public void RepositoryImplementsTheCatalogMediaOwnerPort()
    {
        Assert.Contains(
            typeof(ICatalogMediaRepository),
            typeof(EfCatalogMediaRepository).GetInterfaces());
    }

    [Fact]
    public void InfrastructureDoesNotReferenceAnotherBusinessContextImplementation()
    {
        var references = typeof(EfCatalogMediaRepository).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(reference => reference.StartsWith("Catalog.", StringComparison.Ordinal) ||
                reference.StartsWith("Query.", StringComparison.Ordinal) ||
                reference.StartsWith("Ingestion.", StringComparison.Ordinal) ||
                reference.StartsWith("Analytics.", StringComparison.Ordinal) ||
                reference.StartsWith("Promotion.", StringComparison.Ordinal))
            .ToArray();

        Assert.All(
            references,
            reference => Assert.StartsWith("Catalog.Media.", reference, StringComparison.Ordinal));
    }
}
