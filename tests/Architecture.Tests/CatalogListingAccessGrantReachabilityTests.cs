using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Architecture.Tests;

public sealed class CatalogListingAccessGrantReachabilityTests
{
    [Fact]
    public void ClaimCommandsUseTheRevisionedAccessGrantOwner()
    {
        var repository = RepositoryModel.Load();
        var service = Read(repository, "src/Catalog/Catalog.Application/CatalogClaimService.cs");
        var ports = Read(repository, "src/Catalog/Catalog.Application/CatalogListingAccessGrantPorts.cs");
        var persistence = Read(repository, "src/Catalog/Catalog.Infrastructure/EfCatalogRepository.Claims.cs");
        var composition = Read(
            repository,
            "src/Catalog/Catalog.Infrastructure/CatalogInfrastructureServiceCollectionExtensions.cs");

        Assert.Contains("ICatalogListingAccessGrantRepository accessGrantRepository", service, StringComparison.Ordinal);
        Assert.Equal(2, Count(service, "CatalogListingAccessGrantEventFactory.Create("));
        Assert.Contains("accessGrantRepository.CompleteVerificationAsync(", service, StringComparison.Ordinal);
        Assert.Contains("accessGrantRepository.CompleteRevocationAsync(", service, StringComparison.Ordinal);
        Assert.DoesNotContain("repository.CompleteClaimVerificationAsync(", service, StringComparison.Ordinal);
        Assert.DoesNotContain("repository.SaveClaimDecisionAsync(\n            claim,\n            claimOutboxMessage", service, StringComparison.Ordinal);

        Assert.Contains("public interface ICatalogListingAccessGrantRepository", ports, StringComparison.Ordinal);
        Assert.Contains("Task CompleteVerificationAsync(", ports, StringComparison.Ordinal);
        Assert.Contains("Task CompleteRevocationAsync(", ports, StringComparison.Ordinal);
        Assert.Contains("ICatalogListingAccessGrantRepository>(serviceProvider", composition, StringComparison.Ordinal);

        Assert.Equal(4, Count(persistence, "AddOutbox("));
        Assert.Contains("ApplyGrantMutation(grantRow, grant);", persistence, StringComparison.Ordinal);
        Assert.Contains("The legacy single-event claim verification path is disabled.", persistence, StringComparison.Ordinal);
        Assert.Contains("Claim decision persistence accepts only a rejection", persistence, StringComparison.Ordinal);
    }

    [Fact]
    public void GrantRevisionAndPermissionsAreDatabaseGuarded()
    {
        var repository = RepositoryModel.Load();
        var migration = Read(
            repository,
            "src/Catalog/Catalog.Migrations/Migrations/V017__catalog_listing_access_grant_revision.sql");
        var rows = Read(repository, "src/Catalog/Catalog.Infrastructure/CatalogRows.cs");
        var dbContext = Read(repository, "src/Catalog/Catalog.Infrastructure/CatalogDbContext.cs");

        Assert.Contains("aggregate_revision bigint", migration, StringComparison.Ordinal);
        Assert.Contains("listing_access_grant_revision_positive", migration, StringComparison.Ordinal);
        Assert.Contains("listing_access_grant_revision_state_consistent", migration, StringComparison.Ordinal);
        Assert.Contains("scope BETWEEN 1 AND 7", migration, StringComparison.Ordinal);
        Assert.Contains("public long AggregateRevision { get; set; }", rows, StringComparison.Ordinal);
        Assert.Contains("row.AggregateRevision).IsConcurrencyToken()", dbContext, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducerEventContainsPermissionsButNoPrivateClaimEvidence()
    {
        var repository = RepositoryModel.Load();
        var contract = Read(
            repository,
            "src/Catalog/Catalog.Contracts/CatalogIntegrationEvents.cs");
        var factory = Read(
            repository,
            "src/Catalog/Catalog.Application/CatalogListingAccessGrantEvents.cs");

        Assert.Contains("catalog.listing-access-grant.changed", contract, StringComparison.Ordinal);
        Assert.Contains("aggregator.catalog.listing-access-grant-changed@1", contract, StringComparison.Ordinal);
        Assert.Contains("public sealed record CatalogListingAccessGrantChanged(", contract, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<ListingAccessScopeContract> Permissions,", contract, StringComparison.Ordinal);
        Assert.Contains("long AggregateRevision,", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("EvidenceReference", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("EvidenceDigest", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("ClaimId", contract, StringComparison.Ordinal);
        Assert.Contains("CatalogListingAccessGrantContractMapper.ToContracts(grant.Scopes)", factory, StringComparison.Ordinal);
    }

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
