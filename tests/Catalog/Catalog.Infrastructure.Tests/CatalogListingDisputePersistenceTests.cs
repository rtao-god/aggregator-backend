using Aggregator.Catalog.Application;
using Aggregator.Catalog.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Catalog.Infrastructure.Tests;

public sealed class CatalogListingDisputePersistenceTests
{
    [Fact]
    public void EfModelOwnsOneRevisionedOpenDisputePerListing()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=catalog-dispute-model;Username=test;Password=test")
            .Options;
        using var dbContext = new CatalogDbContext(options);
        var entity = dbContext.Model.FindEntityType(typeof(CatalogListingDisputeRow));

        Assert.NotNull(entity);
        Assert.Equal("listing_dispute", entity.GetTableName());
        Assert.Equal("catalog", entity.GetSchema());
        var aggregateRevision = entity.FindProperty(
            nameof(CatalogListingDisputeRow.AggregateRevision));
        var openReason = entity.FindProperty(
            nameof(CatalogListingDisputeRow.OpenReason));
        var resolutionReason = entity.FindProperty(
            nameof(CatalogListingDisputeRow.ResolutionReason));
        Assert.NotNull(aggregateRevision);
        Assert.NotNull(openReason);
        Assert.NotNull(resolutionReason);
        Assert.True(aggregateRevision.IsConcurrencyToken);
        Assert.Equal(2_000, openReason.GetMaxLength());
        Assert.Equal(2_000, resolutionReason.GetMaxLength());
        var activeIndex = Assert.Single(entity.GetIndexes().Where(index =>
            index.IsUnique &&
            index.Properties.Count == 1 &&
            string.Equals(
                index.Properties[0].Name,
                nameof(CatalogListingDisputeRow.ListingId),
                StringComparison.Ordinal)));
        Assert.Equal("state = 1", activeIndex.GetFilter());
        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(
            typeof(CatalogListingRow),
            foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void MigrationEnforcesImmutableAuditAndPointerActivationGate()
    {
        var source = ReadRepositoryFile(
            "src/Catalog/Catalog.Migrations/Migrations/V016__catalog_listing_dispute.sql");

        Assert.Contains("CREATE TABLE catalog.listing_dispute", source, StringComparison.Ordinal);
        Assert.Contains("ux_catalog_listing_dispute_open", source, StringComparison.Ordinal);
        Assert.Contains("WHERE state = 1", source, StringComparison.Ordinal);
        Assert.Contains("trg_catalog_listing_dispute_lifecycle", source, StringComparison.Ordinal);
        Assert.Contains("OLD.state = 2", source, StringComparison.Ordinal);
        Assert.Contains("NEW.aggregate_revision <> OLD.aggregate_revision + 1", source, StringComparison.Ordinal);
        Assert.Contains("trg_catalog_current_publication_dispute_gate", source, StringComparison.Ordinal);
        Assert.Contains("dispute.state = 1", source, StringComparison.Ordinal);
        Assert.Contains("ERRCODE = 'P7604'", source, StringComparison.Ordinal);
        Assert.Contains(
            "Resolve every open Catalog listing dispute before activating this publication.",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryPublishesEligibilityEffectsAndBlocksCreateAndRollback()
    {
        var disputes = ReadRepositoryFile(
            "src/Catalog/Catalog.Infrastructure/EfCatalogRepository.ListingDisputes.cs");
        var publications = ReadRepositoryFile(
            "src/Catalog/Catalog.Infrastructure/EfCatalogRepository.Publications.cs");

        Assert.Contains("FOR UPDATE", disputes, StringComparison.Ordinal);
        Assert.Contains("hasBlockingDispute: true", disputes, StringComparison.Ordinal);
        Assert.Contains("hasBlockingDispute: false", disputes, StringComparison.Ordinal);
        Assert.Equal(2, Count(disputes, "AddOutbox(eligibilityOutbox)"));
        Assert.Equal(2, Count(publications, "EnsureNoOpenListingDisputesAsync("));
        Assert.Contains("PublicationListingDisputeSqlState = \"P7604\"", publications, StringComparison.Ordinal);
        Assert.Contains("CatalogPublicationActivationBlockReason.ListingDispute", publications, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveLocksListingBeforeDisputeState()
    {
        var source = ReadRepositoryFile(
            "src/Catalog/Catalog.Infrastructure/EfCatalogRepository.ListingDisputes.cs");
        var saveStart = source.IndexOf(
            "public async Task<ListingDispute> SaveAsync(",
            StringComparison.Ordinal);
        var listingLock = source.IndexOf(
            "var listingRow = await RequireLockedListingAsync(",
            saveStart,
            StringComparison.Ordinal);
        var disputeLock = source.IndexOf(
            "FROM catalog.listing_dispute",
            saveStart,
            StringComparison.Ordinal);

        Assert.True(saveStart >= 0);
        Assert.True(listingLock > saveStart);
        Assert.True(disputeLock > listingLock);
        Assert.Contains(
            "Every dispute/publication path locks the listing before its dispute state.",
            source,
            StringComparison.Ordinal);
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

    private static string ReadRepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AggregatorBackend.slnx")))
            {
                return File.ReadAllText(Path.Combine(
                    current.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Aggregator backend repository root was not found.");
    }
}
