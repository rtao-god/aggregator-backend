namespace Ingestion.Infrastructure.Tests;

public sealed class IngestionReviewCommitMigrationTests
{
    [Fact]
    public void ReviewCommitMigrationOwnsExactMutableAndImmutableRecords()
    {
        var sql = ReadMigration();

        Assert.Contains(
            "CREATE TABLE batches.item_decision_current",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE batches.item_decision_history",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE batches.commit_selection",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TABLE batches.catalog_delivery_outcome",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "FOREIGN KEY (batch_id, item_key)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "REFERENCES batches.commit_selection (batch_id, item_key)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "trg_item_decision_history_immutable",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "trg_commit_selection_immutable",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "trg_catalog_delivery_outcome_immutable",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ON DELETE CASCADE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeliveredAndRejectedCatalogOutcomesAreMutuallyExclusive()
    {
        var sql = ReadMigration();
        var identityConstraint = Slice(
            sql,
            "CONSTRAINT ck_catalog_delivery_outcome_identity",
            "CONSTRAINT ck_catalog_delivery_outcome_actor");

        Assert.Contains("outcome = 1", identityConstraint, StringComparison.Ordinal);
        Assert.Contains("catalog_subject_id IS NOT NULL", identityConstraint, StringComparison.Ordinal);
        Assert.Contains("catalog_listing_id IS NOT NULL", identityConstraint, StringComparison.Ordinal);
        Assert.Contains("catalog_listing_revision_id IS NOT NULL", identityConstraint, StringComparison.Ordinal);
        Assert.Contains("failure_code IS NULL", identityConstraint, StringComparison.Ordinal);
        Assert.Contains("outcome = 2", identityConstraint, StringComparison.Ordinal);
        Assert.Contains("catalog_subject_id IS NULL", identityConstraint, StringComparison.Ordinal);
        Assert.Contains("catalog_listing_id IS NULL", identityConstraint, StringComparison.Ordinal);
        Assert.Contains("catalog_listing_revision_id IS NULL", identityConstraint, StringComparison.Ordinal);
        Assert.Contains("failure_code IS NOT NULL", identityConstraint, StringComparison.Ordinal);
    }

    private static string ReadMigration()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "src",
            "Ingestion",
            "Ingestion.Migrations",
            "Migrations",
            "V002__ingestion_review_commit.sql");
        Assert.True(File.Exists(path), $"Expected migration was not found: {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AggregatorBackend.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Repository root containing AggregatorBackend.slnx was not found.");
    }

    private static string Slice(string value, string startToken, string endToken)
    {
        var start = value.IndexOf(startToken, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start token was not found: {startToken}");
        var end = value.IndexOf(endToken, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End token was not found after start: {endToken}");
        return value[start..end];
    }
}
