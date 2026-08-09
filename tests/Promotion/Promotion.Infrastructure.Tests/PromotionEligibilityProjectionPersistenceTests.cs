namespace Promotion.Infrastructure.Tests;

public sealed class PromotionEligibilityProjectionPersistenceTests
{
    [Fact]
    public void MigrationRejectsUntrackedRowsAndAddsExactInboxLineage()
    {
        var migration = ReadRepositoryFile(
            "src/Promotion/Promotion.Migrations/Migrations/V004__catalog_listing_eligibility_inbox.sql");

        Assert.Contains(
            "listing_eligibility_projection contains rows without producer inbox lineage",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE messaging.inbox_message",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "ux_promotion_inbox_listing_revision",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "published_listing_revision_id",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "fk_promotion_eligibility_source_message",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "ck_promotion_eligibility_unpublished_shape",
            migration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectionStoreUsesAtomicInboxAndMonotonicListingCheckpoint()
    {
        var store = ReadRepositoryFile(
            "src/Promotion/Promotion.Infrastructure/PostgresPromotionEligibilityProjectionStore.cs");

        Assert.Contains("IsolationLevel.Serializable", store, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", store, StringComparison.Ordinal);
        Assert.Contains(
            "PROMOTION_ELIGIBILITY_INBOX_MESSAGE_CORRUPT",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROMOTION_ELIGIBILITY_REVISION_GAP",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "INSERT INTO messaging.inbox_message",
            store,
            StringComparison.Ordinal);
        Assert.Contains(
            "UPDATE access_projection.listing_eligibility_projection",
            store,
            StringComparison.Ordinal);
        Assert.Contains("await transaction.CommitAsync", store, StringComparison.Ordinal);
    }

    [Fact]
    public void PlacementReconciliationSerializesWithTheEligibilityStream()
    {
        var reconciliation = ReadRepositoryFile(
            "src/Promotion/Promotion.Infrastructure/EfPromotionRepository.EligibilityReconciliation.cs");
        var registration = ReadRepositoryFile(
            "src/Promotion/Promotion.Infrastructure/PromotionInfrastructureServiceCollectionExtensions.cs");

        Assert.Contains("IsolationLevel.Serializable", reconciliation, StringComparison.Ordinal);
        Assert.Contains(
            "pg_advisory_xact_lock(hashtextextended",
            reconciliation,
            StringComparison.Ordinal);
        Assert.Contains("currentEligibility.SourceRevision >", reconciliation, StringComparison.Ordinal);
        Assert.Contains("currentEligibility.SourceRevision <", reconciliation, StringComparison.Ordinal);
        Assert.Contains("EnsureCurrentEligibilityMatches", reconciliation, StringComparison.Ordinal);
        Assert.Contains("PauseWhenCatalogIneligible", reconciliation, StringComparison.Ordinal);
        Assert.Contains("PlacementCapacity.RemoveRange", reconciliation, StringComparison.Ordinal);
        Assert.Contains("PromotionOutboxMessageFactory.Create", reconciliation, StringComparison.Ordinal);
        Assert.Contains("await _dbContext.SaveChangesAsync", reconciliation, StringComparison.Ordinal);
        Assert.Contains("await transaction.CommitAsync", reconciliation, StringComparison.Ordinal);
        Assert.DoesNotContain(".Resume(", reconciliation, StringComparison.Ordinal);
        Assert.Contains(
            "AddScoped<IPromotionEligibilityPlacementReconciler>",
            registration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BusinessRepositoryCannotWriteEligibilityProjection()
    {
        var port = ReadRepositoryFile(
            "src/Promotion/Promotion.Application/PromotionApplicationPorts.cs");
        var repository = ReadRepositoryFile(
            "src/Promotion/Promotion.Infrastructure/EfPromotionRepository.Eligibility.cs");

        Assert.DoesNotContain("UpsertEligibilityAsync", port, StringComparison.Ordinal);
        Assert.DoesNotContain("UpsertEligibilityAsync", repository, StringComparison.Ordinal);
        Assert.Contains("GetEligibilityAsync", port, StringComparison.Ordinal);
        Assert.Contains("GetEligibilityAsync", repository, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Repository root was not found.");
        return File.ReadAllText(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
