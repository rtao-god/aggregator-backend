namespace Architecture.Tests;

public sealed class BackupRestoreProofWindowsEntrypointTests
{
    [Fact]
    public void WindowsEntrypointDelegatesToCanonicalPythonOrchestrator()
    {
        var source = Read("tools/run-backup-restore-proof.ps1");

        Assert.Contains(
            "run-backup-restore-proof.py",
            source,
            StringComparison.Ordinal);
        Assert.Contains("'--repository-root'", source, StringComparison.Ordinal);
        Assert.Contains("$repositoryRoot", source, StringComparison.Ordinal);
        Assert.Contains("$RemainingArguments", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "restore-proof.sh",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "pg_dump",
            source,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsEntrypointRequiresPythonThreeAndPropagatesFailure()
    {
        var source = Read("tools/run-backup-restore-proof.ps1");

        Assert.Contains("Get-Command py", source, StringComparison.Ordinal);
        Assert.Contains("@('-3')", source, StringComparison.Ordinal);
        Assert.Contains("Get-Command python", source, StringComparison.Ordinal);
        Assert.Contains(
            "Python 3 is required to execute the backup/restore proof.",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($LASTEXITCODE -ne 0)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Backup/restore proof failed with exit code $LASTEXITCODE.",
            source,
            StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Repository file '{relativePath}' was not found.");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
