namespace Query.Infrastructure.Tests;

public sealed class QuerySitemapStoreContractTests
{
    private static readonly string ProjectionStore = ReadRepositoryFile(
        "src/Query/Query.Infrastructure/PostgresPublicSitemapProjectionStore.cs");
    private static readonly string ReadStore = ReadRepositoryFile(
        "src/Query/Query.Infrastructure/PostgresPublicSitemapStore.cs");

    [Fact]
    public void ActivationUsesSerializableCatalogLockAndExpectedPointer()
    {
        Assert.Contains("IsolationLevel.Serializable", ProjectionStore, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", ProjectionStore, StringComparison.Ordinal);
        Assert.Contains("query-sitemap:", ProjectionStore, StringComparison.Ordinal);
        Assert.Contains("EnsureExpectedPointer", ProjectionStore, StringComparison.Ordinal);
        Assert.Contains("QUERY_SITEMAP_POINTER_CONFLICT", ProjectionStore, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", ProjectionStore, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivationCommitsRevisionRowsBeforePointerInOneTransaction()
    {
        var insertRevision = ProjectionStore.IndexOf("InsertRevisionAsync", StringComparison.Ordinal);
        var insertRecords = ProjectionStore.IndexOf("InsertRecordsAsync", StringComparison.Ordinal);
        var insertHreflang = ProjectionStore.IndexOf("InsertHreflangAsync", StringComparison.Ordinal);
        var activatePointer = ProjectionStore.IndexOf("ActivatePointerAsync", StringComparison.Ordinal);
        var commit = ProjectionStore.IndexOf("CommitAsync", activatePointer, StringComparison.Ordinal);

        Assert.True(insertRevision >= 0);
        Assert.True(insertRecords > insertRevision);
        Assert.True(insertHreflang > insertRecords);
        Assert.True(activatePointer > insertHreflang);
        Assert.True(commit > activatePointer);
    }

    [Fact]
    public void ReadStoreUsesOneRepeatableReadSnapshotAndKeysetPagination()
    {
        Assert.Contains("IsolationLevel.RepeatableRead", ReadStore, StringComparison.Ordinal);
        Assert.Contains("locale > @last_locale", ReadStore, StringComparison.Ordinal);
        Assert.Contains("path > @last_path", ReadStore, StringComparison.Ordinal);
        Assert.Contains("ORDER BY locale, path", ReadStore, StringComparison.Ordinal);
        Assert.Contains("checked(request.PageSize + 1)", ReadStore, StringComparison.Ordinal);
        Assert.Contains("request.Cursor.PublicReadRevisionId != activeRevisionId.Value", ReadStore, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicReadStoreHasNoMutationOrRepairPath()
    {
        Assert.DoesNotContain("INSERT INTO", ReadStore, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", ReadStore, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", ReadStore, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("repair", ReadStore, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("latest", ReadStore, StringComparison.OrdinalIgnoreCase);
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
