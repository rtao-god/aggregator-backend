using Aggregator.CatalogMedia.Domain;

namespace Catalog.Media.Domain.Tests;

public sealed class CatalogMediaDomainBoundaryTests
{
    [Fact]
    public void DomainAssemblyHasNoPersistenceOrHttpFrameworkDependency()
    {
        var references = typeof(CatalogMediaAsset).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(
            references,
            reference => reference.Contains("EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(
            references,
            reference => reference.Contains("AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(
            references,
            reference => reference.Contains("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void MediaLifecycleExposesExplicitOwnerStates()
    {
        var states = Enum.GetNames<CatalogMediaState>();

        Assert.Contains("Registered", states);
        Assert.Contains("Uploaded", states);
        Assert.Contains("Scanning", states);
        Assert.Contains("Accepted", states);
        Assert.Contains("Rejected", states);
        Assert.Contains("RightsRevoked", states);
    }
}
