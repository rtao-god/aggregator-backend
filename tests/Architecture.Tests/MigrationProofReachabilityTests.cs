namespace Architecture.Tests;

public sealed class MigrationProofReachabilityTests
{
    [Fact]
    public void MigrationProofCoversEveryCanonicalDatabaseOwner()
    {
        var source = Read("tools/run-migration-proof.py");

        foreach (var context in new[]
                 {
                     "catalog",
                     "query",
                     "ingestion",
                     "analytics",
                     "promotion",
                 })
        {
            Assert.Contains($"\"{context}\"", source, StringComparison.Ordinal);
            Assert.Contains(
                "context: f\"{context}-migrate\"",
                source,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "ensure_migration_services(configuration, contexts)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "dependency_closure(configuration, migration_services)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationProofUsesAnIsolatedComposeProjectAndCleansOnlyItsOwnVolumes()
    {
        var source = Read("tools/run-migration-proof.py");

        Assert.Contains(
            "aggregator-migration-proof-",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"--project-name\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"down\",\n                        \"--volumes\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"--remove-orphans\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if not arguments.keep_project:",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "docker compose down -v",
            source,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrationProofRunsEveryOwnerTwiceAndFailsOnAnyMissingPass()
    {
        var source = Read("tools/run-migration-proof.py");

        Assert.Contains(
            "for pass_number in (1, 2):",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "expected_pass_count = len(contexts) * 2",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if failure is None and len(migration_passes) != expected_pass_count:",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"run\", \"--rm\", \"--no-deps\", service",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationProofRetainsExactCommandLogsAndDigests()
    {
        var source = Read("tools/run-migration-proof.py");

        Assert.Contains("class CommandRecord", source, StringComparison.Ordinal);
        Assert.Contains("log_sha256", source, StringComparison.Ordinal);
        Assert.Contains(
            "hashlib.sha256(output.encode(\"utf-8\")).hexdigest()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "schema_identity=\"aggregator-backend/migration-proof@1\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "json.dumps(payload, indent=2, sort_keys=True)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("--self-test", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationProofRequiresExplicitEnvironmentAndNeverMutatesApplicationStartup()
    {
        var source = Read("tools/run-migration-proof.py");
        var apiPrograms = Directory
            .EnumerateFiles(
                Path.Combine(FindRepositoryRoot(), "src"),
                "Program.cs",
                SearchOption.AllDirectories)
            .Where(path => path.Contains(".Api", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();

        Assert.Contains(
            "Create it explicitly from .env.example and provide required secrets.",
            source,
            StringComparison.Ordinal);
        Assert.All(
            apiPrograms,
            program => Assert.DoesNotContain(
                "run-migration-proof.py",
                program,
                StringComparison.Ordinal));
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
