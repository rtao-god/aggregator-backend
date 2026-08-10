namespace Query.Infrastructure.Tests;

public sealed class QuerySitemapRevisionPointerMigrationTests
{
    private static readonly string Migration = ReadRepositoryFile(
        "src/Query/Query.Migrations/Migrations/V015__query_sitemap_revision_pointer.sql");

    [Fact]
    public void RevisionManifestIsImmutableAndDigestBound()
    {
        Assert.Contains("CREATE TABLE seo_projection.sitemap_revision", Migration, StringComparison.Ordinal);
        Assert.Contains("content_digest char(64) NOT NULL", Migration, StringComparison.Ordinal);
        Assert.Contains("content_digest ~ '^[0-9a-f]{64}$'", Migration, StringComparison.Ordinal);
        Assert.Contains("record_count integer NOT NULL", Migration, StringComparison.Ordinal);
        Assert.Contains("trg_query_sitemap_revision_immutable", Migration, StringComparison.Ordinal);
        Assert.Contains("ERRCODE = 'P7611'", Migration, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivePointerReferencesExactCatalogRevision()
    {
        Assert.Contains("CREATE TABLE seo_projection.active_sitemap_revision", Migration, StringComparison.Ordinal);
        Assert.Contains(
            "FOREIGN KEY (catalog_key, public_read_revision_id)",
            Migration,
            StringComparison.Ordinal);
        Assert.Contains("verify_active_sitemap_revision", Migration, StringComparison.Ordinal);
        Assert.Contains("actual_count <> expected_count", Migration, StringComparison.Ordinal);
        Assert.Contains("ERRCODE = 'P7610'", Migration, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivePointerCannotBeSilentlyRemoved()
    {
        Assert.Contains("reject_active_sitemap_delete", Migration, StringComparison.Ordinal);
        Assert.Contains("BEFORE DELETE ON seo_projection.active_sitemap_revision", Migration, StringComparison.Ordinal);
        Assert.Contains("ERRCODE = 'P7612'", Migration, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
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
