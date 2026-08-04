using Aggregator.CatalogMedia.Application;

namespace Catalog.Media.Application.Tests;

public sealed class CatalogMediaApplicationBoundaryTests
{
    [Fact]
    public void ProcessingLeaseCapturesExactStoredAggregateRevision()
    {
        var property = typeof(CatalogMediaProcessingLease)
            .GetProperty(nameof(CatalogMediaProcessingLease.StoredAggregateRevision));

        Assert.NotNull(property);
        Assert.Equal(typeof(long), property.PropertyType);
    }

    [Fact]
    public void RepositoryPortHasNoPublicationAuthority()
    {
        var methods = typeof(ICatalogMediaRepository).GetMethods();

        Assert.DoesNotContain(
            methods,
            method => method.Name.Contains("Publish", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            methods,
            method => method.Name.Contains("Publication", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CanonicalJsonDigestDoesNotDependOnDictionaryInsertionOrder()
    {
        var first = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["b"] = "second",
            ["a"] = "first",
        };
        var second = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a"] = "first",
            ["b"] = "second",
        };

        Assert.Equal(
            CatalogMediaCanonicalJson.ComputeDigest(first),
            CatalogMediaCanonicalJson.ComputeDigest(second));
    }
}
