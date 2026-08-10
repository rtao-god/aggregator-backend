namespace Query.Infrastructure.Tests;

public sealed class QuerySitemapProjectionMigrationTests
{
    private static readonly string Migration = ReadRepositoryFile(
        "src/Query/Query.Migrations/Migrations/V013__query_sitemap_projection.sql");

    [Fact]
    public void SitemapRowsAreRevisionBoundAndImmutable()
    {
        Assert.Contains("public_read_revision_id uuid NOT NULL", Migration, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (public_read_revision_id, catalog_key, locale, path)", Migration, StringComparison.Ordinal);
        Assert.Contains("trg_query_sitemap_record_immutable", Migration, StringComparison.Ordinal);
        Assert.Contains("trg_query_sitemap_hreflang_immutable", Migration, StringComparison.Ordinal);
        Assert.Contains("ERRCODE = 'P7606'", Migration, StringComparison.Ordinal);
    }

    [Fact]
    public void SitemapRejectsNonIndexableAndNonCanonicalPaths()
    {
        Assert.Contains("path = canonical_path", Migration, StringComparison.Ordinal);
        Assert.Contains("position('?' IN path) = 0", Migration, StringComparison.Ordinal);
        Assert.Contains("position('#' IN path) = 0", Migration, StringComparison.Ordinal);
        Assert.Contains("path NOT LIKE '%//%'", Migration, StringComparison.Ordinal);
        Assert.Contains("path !~ '(^|/)\\.\\.?(/|$)'", Migration, StringComparison.Ordinal);
    }

    [Fact]
    public void HreflangRequiresExactTargetsSelfLinksAndReciprocity()
    {
        Assert.Contains("fk_query_sitemap_hreflang_source", Migration, StringComparison.Ordinal);
        Assert.Contains("fk_query_sitemap_hreflang_target", Migration, StringComparison.Ordinal);
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", Migration, StringComparison.Ordinal);
        Assert.Contains("missing its exact self hreflang link", Migration, StringComparison.Ordinal);
        Assert.Contains("hreflang group is not reciprocal", Migration, StringComparison.Ordinal);
        Assert.Contains("ERRCODE = 'P7607'", Migration, StringComparison.Ordinal);
        Assert.Contains("ERRCODE = 'P7608'", Migration, StringComparison.Ordinal);
    }

    [Fact]
    public void SitemapHasStableRevisionCatalogLocalePathPaginationIndex()
    {
        Assert.Contains(
            "(public_read_revision_id, catalog_key, locale, path)",
            Migration,
            StringComparison.Ordinal);
        Assert.DoesNotContain("latest", Migration, StringComparison.OrdinalIgnoreCase);
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
