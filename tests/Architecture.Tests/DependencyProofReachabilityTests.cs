namespace Architecture.Tests;

public sealed class DependencyProofReachabilityTests
{
    [Fact]
    public void DependencyProofUsesPinnedSolutionAndCentralPackageOwners()
    {
        var source = Read("tools/run-dependency-proof.py");

        Assert.Contains(
            "SOLUTION = \"AggregatorBackend.slnx\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "GLOBAL_JSON = \"global.json\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CENTRAL_PACKAGES = \"Directory.Packages.props\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "solution_sha256=file_sha256(solution)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "global_json_sha256=file_sha256(global_json)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "central_packages_sha256=file_sha256(central_packages)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyProofFailsNuGetRestoreOnEveryVulnerabilitySeverity()
    {
        var source = Read("tools/run-dependency-proof.py");

        Assert.Contains("-p:NuGetAudit=true", source, StringComparison.Ordinal);
        Assert.Contains("-p:NuGetAuditMode=all", source, StringComparison.Ordinal);
        Assert.Contains(
            "-p:WarningsAsErrors=NU1901;NU1902;NU1903;NU1904",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "--vulnerable",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "--include-transitive",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if vulnerability_count != 0:",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyProofBuildsInventoryOnlyFromDotnetPackageJson()
    {
        var source = Read("tools/run-dependency-proof.py");

        Assert.Contains(
            "\"dotnet\",\n                \"package\",\n                \"list\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains("\"--format\",\n                \"json\"", source, StringComparison.Ordinal);
        Assert.Contains("\"--output-version\",\n                \"1\"", source, StringComparison.Ordinal);
        Assert.Contains("topLevelPackages", source, StringComparison.Ordinal);
        Assert.Contains("transitivePackages", source, StringComparison.Ordinal);
        Assert.Contains(
            "collect_components(inventory)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Directory.EnumerateFiles",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "project.assets.json",
            source,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DependencyProofGeneratesACommitBoundCycloneDxSbom()
    {
        var source = Read("tools/run-dependency-proof.py");

        Assert.Contains("\"bomFormat\": \"CycloneDX\"", source, StringComparison.Ordinal);
        Assert.Contains("\"specVersion\": \"1.6\"", source, StringComparison.Ordinal);
        Assert.Contains("uuid.uuid5(", source, StringComparison.Ordinal);
        Assert.Contains(
            "pkg:github/rtao-god/aggregator-backend@{source_commit}",
            source,
            StringComparison.Ordinal);
        Assert.Contains("pkg:nuget/", source, StringComparison.Ordinal);
        Assert.Contains(
            "aggregator-backend:dependency-kind",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "aggregator-backend:referencing-projects",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "validate_sbom(sbom, len(components))",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyProofRetainsInventorySbomAndExactCommandEvidence()
    {
        var source = Read("tools/run-dependency-proof.py");

        Assert.Contains(
            "dependency-inventory.json",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "aggregator-backend.cdx.json",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "dependency_inventory_sha256=inventory_sha256",
            source,
            StringComparison.Ordinal);
        Assert.Contains("sbom_sha256=sbom_sha256", source, StringComparison.Ordinal);
        Assert.Contains("restore_audit_command", source, StringComparison.Ordinal);
        Assert.Contains("inventory_command", source, StringComparison.Ordinal);
        Assert.Contains("vulnerability_command", source, StringComparison.Ordinal);
        Assert.Contains(
            "schema_identity=\"aggregator-backend/dependency-proof@1\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyProofUsesExactCleanSourceAndBoundedProcessRuntime()
    {
        var source = Read("tools/run-dependency-proof.py");

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
        Assert.Contains("--command-timeout-seconds", source, StringComparison.Ordinal);
        Assert.Contains("--allow-dirty", source, StringComparison.Ordinal);
        Assert.Contains("--self-test", source, StringComparison.Ordinal);
        Assert.Contains(
            "release_valid = failure is None and source_identity.tree_clean and not arguments.allow_dirty",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProofCommandRunner(",
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
