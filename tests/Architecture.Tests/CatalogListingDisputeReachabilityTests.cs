using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

public sealed class CatalogListingDisputeReachabilityTests
{
    [Fact]
    public void CatalogCompositionRegistersOneDisputeOwner()
    {
        var repository = RepositoryModel.Load();
        var application = Read(
            repository,
            "src/Catalog/Catalog.Application/CatalogApplicationServiceCollectionExtensions.cs");
        var infrastructure = Read(
            repository,
            "src/Catalog/Catalog.Infrastructure/CatalogInfrastructureServiceCollectionExtensions.cs");
        var repositoryDeclaration = Read(
            repository,
            "src/Catalog/Catalog.Infrastructure/EfCatalogRepository.cs");

        Assert.Contains("AddScoped<CatalogListingDisputeService>()", application, StringComparison.Ordinal);
        Assert.Contains("AddScoped<ICatalogListingDisputeRepository>", infrastructure, StringComparison.Ordinal);
        Assert.Contains("ICatalogListingDisputeRepository", repositoryDeclaration, StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountProductionOccurrences(
                repository,
                "class CatalogListingDisputeService"));
        Assert.Equal(
            1,
            CountProductionOccurrences(
                repository,
                "interface ICatalogListingDisputeRepository"));
    }

    [Fact]
    public void HttpBoundaryUsesReviewAuthorizationAndExactResources()
    {
        var repository = RepositoryModel.Load();
        var controller = Read(
            repository,
            "src/Catalog/Catalog.Api/CatalogListingDisputesController.cs");
        var operations = Read(
            repository,
            "src/Catalog/Catalog.Api/CatalogApiContracts.cs");

        Assert.Contains(
            "[Route(\"api/catalog-command/listings/{listingId:guid}/disputes\")]",
            controller,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            Count(controller, "[Authorize(Policy = CatalogAuthorizationPolicies.Review)]"));
        Assert.Contains("OpenAsync(", controller, StringComparison.Ordinal);
        Assert.Contains("ResolveAsync(", controller, StringComparison.Ordinal);
        Assert.Contains("CatalogActorAccessor.Require(HttpContext)", controller, StringComparison.Ordinal);
        Assert.Contains("CatalogEventContextAccessor.Require(correlation)", controller, StringComparison.Ordinal);
        Assert.Contains("OpenCatalogListingDispute", operations, StringComparison.Ordinal);
        Assert.Contains("ResolveCatalogListingDispute", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void DisputeEffectsRemainInsideCatalogAndUseProducerOwnedEvents()
    {
        var repository = RepositoryModel.Load();
        var disputePersistence = Read(
            repository,
            "src/Catalog/Catalog.Infrastructure/EfCatalogRepository.ListingDisputes.cs");
        var publicationPersistence = Read(
            repository,
            "src/Catalog/Catalog.Infrastructure/EfCatalogRepository.Publications.cs");

        Assert.Contains("CreateListingPromotionEligibilityOutboxAsync(", disputePersistence, StringComparison.Ordinal);
        Assert.Contains("hasBlockingDispute: true", disputePersistence, StringComparison.Ordinal);
        Assert.Contains("hasBlockingDispute: false", disputePersistence, StringComparison.Ordinal);
        Assert.Equal(2, Count(publicationPersistence, "EnsureNoOpenListingDisputesAsync("));
        Assert.Contains("CatalogPublicationActivationBlockReason.ListingDispute", publicationPersistence, StringComparison.Ordinal);

        foreach (var source in Directory.EnumerateFiles(
                     Path.Combine(repository.Root, "src", "Catalog"),
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(source);
            Assert.DoesNotContain("Aggregator.Promotion.Application", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Aggregator.Promotion.Domain", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Aggregator.Promotion.Infrastructure", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CatalogDoesNotReferencePromotionProjects()
    {
        var repository = RepositoryModel.Load();
        var catalogProjects = repository.Projects
            .Where(project => repository.Relative(project).StartsWith("src/Catalog/", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var forbidden = repository.References
            .Where(reference =>
                catalogProjects.Contains(reference.Source) &&
                repository.Relative(reference.Target).StartsWith("src/Promotion/", StringComparison.OrdinalIgnoreCase))
            .Select(reference => $"{repository.Relative(reference.Source)} -> {repository.Relative(reference.Target)}")
            .ToArray();

        Assert.Empty(forbidden);
    }

    private static int CountProductionOccurrences(RepositoryModel repository, string marker) =>
        Directory.EnumerateFiles(
                Path.Combine(repository.Root, "src", "Catalog"),
                "*.cs",
                SearchOption.AllDirectories)
            .Sum(path => Count(File.ReadAllText(path), marker));

    private static int Count(string value, string marker)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += marker.Length;
        }

        return count;
    }

    private static string Read(RepositoryModel repository, string relativePath) =>
        File.ReadAllText(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
