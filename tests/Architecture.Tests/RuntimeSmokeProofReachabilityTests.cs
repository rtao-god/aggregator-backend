namespace Architecture.Tests;

public sealed class RuntimeSmokeProofReachabilityTests
{
    [Fact]
    public void RuntimeSmokeCoversEveryMigrationAndRuntimeDeployable()
    {
        var source = Read("tools/run-runtime-smoke-proof.py");

        foreach (var service in new[]
                 {
                     "catalog-migrate",
                     "query-migrate",
                     "ingestion-migrate",
                     "analytics-migrate",
                     "promotion-migrate",
                     "catalog-api",
                     "catalog-worker",
                     "catalog-media-worker",
                     "query-api",
                     "query-worker",
                     "ingestion-api",
                     "ingestion-worker",
                     "analytics-api",
                     "analytics-worker",
                     "promotion-api",
                     "promotion-worker",
                     "reverse-proxy",
                 })
        {
            Assert.Contains($"\"{service}\"", source, StringComparison.Ordinal);
        }

        Assert.Contains(
            "for service in MIGRATION_SERVICES:",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "*RUNTIME_SERVICES",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSmokeRequiresHealthchecksForEveryApiHost()
    {
        var source = Read("tools/run-runtime-smoke-proof.py");

        foreach (var service in new[]
                 {
                     "catalog-api",
                     "query-api",
                     "ingestion-api",
                     "analytics-api",
                     "promotion-api",
                 })
        {
            Assert.Contains($"\"{service}\"", source, StringComparison.Ordinal);
        }

        Assert.Contains(
            "service in HEALTHCHECK_REQUIRED_SERVICES",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "has no active Compose healthcheck",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "normalized_health != \"healthy\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSmokeRunsMigrationsBeforeStartingApplicationHosts()
    {
        var source = Read("tools/run-runtime-smoke-proof.py");
        var migrationIndex = source.IndexOf(
            "for service in MIGRATION_SERVICES:",
            StringComparison.Ordinal);
        var runtimeIndex = source.IndexOf(
            "runtime_start_record, _ = runner.run(",
            StringComparison.Ordinal);

        Assert.True(migrationIndex >= 0, "Migration execution block was not found.");
        Assert.True(runtimeIndex > migrationIndex, "Runtime startup must follow migrations.");
        Assert.Contains(
            "\"run\", \"--rm\", \"--no-deps\", service",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"--wait-timeout\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSmokeProvesAStableOneContainerPerDeployableState()
    {
        var source = Read("tools/run-runtime-smoke-proof.py");

        Assert.Contains(
            "time.sleep(stability_seconds)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"ps\", \"--all\", \"--format\", \"json\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if len(matches) != 1:",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if state != \"running\":",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "len(runtime_evidence) != len(RUNTIME_SERVICES)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSmokeRetainsFailureDiagnosticsAndAlwaysScopesCleanup()
    {
        var source = Read("tools/run-runtime-smoke-proof.py");

        Assert.Contains(
            "capture-runtime-diagnostics",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"--tail\",\n                    \"500\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "make_compose_project_name(\"aggregator-runtime-smoke\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"down\",\n                        \"--volumes\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if not arguments.keep_project:",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSmokeBindsReportToExactSourceAndBoundedExecution()
    {
        var source = Read("tools/run-runtime-smoke-proof.py");

        Assert.Contains(
            "source_identity = read_source_identity(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "source_commit=source_identity.commit_sha",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "source_tree_clean=source_identity.tree_clean",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "schema_identity=\"aggregator-backend/runtime-smoke-proof@1\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains("--command-timeout-seconds", source, StringComparison.Ordinal);
        Assert.Contains("--startup-timeout-seconds", source, StringComparison.Ordinal);
        Assert.Contains("--stability-seconds", source, StringComparison.Ordinal);
        Assert.Contains("--self-test", source, StringComparison.Ordinal);
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
