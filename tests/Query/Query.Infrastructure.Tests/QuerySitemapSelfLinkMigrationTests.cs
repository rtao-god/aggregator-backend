namespace Query.Infrastructure.Tests;

public sealed class QuerySitemapSelfLinkMigrationTests
{
    [Fact]
    public void EverySitemapRecordRequiresAnExactDeferredSelfLink()
    {
        var migration = ReadRepositoryFile(
            "src/Query/Query.Migrations/Migrations/V014__query_sitemap_record_self_link.sql");

        Assert.Contains("verify_sitemap_record_self_link", migration, StringComparison.Ordinal);
        Assert.Contains("AFTER INSERT ON seo_projection.sitemap_record", migration, StringComparison.Ordinal);
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", migration, StringComparison.Ordinal);
        Assert.Contains("self_link.source_locale = NEW.locale", migration, StringComparison.Ordinal);
        Assert.Contains("self_link.source_path = NEW.path", migration, StringComparison.Ordinal);
        Assert.Contains("self_link.alternate_locale = NEW.locale", migration, StringComparison.Ordinal);
        Assert.Contains("self_link.alternate_path = NEW.path", migration, StringComparison.Ordinal);
        Assert.Contains("ERRCODE = 'P7609'", migration, StringComparison.Ordinal);
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
