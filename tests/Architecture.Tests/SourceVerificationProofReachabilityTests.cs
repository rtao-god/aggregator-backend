namespace Architecture.Tests;

public sealed class SourceVerificationProofReachabilityTests
{
    [Fact]
    public void SourceProofDelegatesToCanonicalContractAndTestOwners()
    {
        var source = Read("tools/run-source-verification-proof.py");

        Assert.Contains(
            "CONTRACT_VERIFIER = \"tools/verify-contracts.py\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TEST_DISCOVERY_GUARD = \"tools/run-tests-with-discovery-guard.py\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SOLUTION = \"AggregatorBackend.slnx\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[sys.executable, str(contract_verifier)]",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[sys.executable, str(test_discovery_guard)]",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceProofNeverBypassesTheTestDiscoveryGuard()
    {
        var source = Read("tools/run-source-verification-proof.py");

        Assert.DoesNotContain(
            "\"dotnet\",\n                \"test\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "run-guarded-tests",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "test_command, _ = runner.run(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceProofRunsContractsThenBuildThenGuardedTests()
    {
        var source = Read("tools/run-source-verification-proof.py");
        var contractIndex = source.IndexOf(
            "contract_command, _ = runner.run(",
            StringComparison.Ordinal);
        var buildIndex = source.IndexOf(
            "build_command, _ = runner.run(",
            StringComparison.Ordinal);
        var testIndex = source.IndexOf(
            "test_command, _ = runner.run(",
            StringComparison.Ordinal);

        Assert.True(contractIndex >= 0, "Contract verification command was not found.");
        Assert.True(buildIndex > contractIndex, "Build must follow contract verification.");
        Assert.True(testIndex > buildIndex, "Guarded tests must follow build.");
        Assert.Contains("\"/m:1\"", source, StringComparison.Ordinal);
        Assert.Contains("\"/nr:false\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceProofBindsEvidenceToExactSourceAndOwnerDigests()
    {
        var source = Read("tools/run-source-verification-proof.py");

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
            "contract_verifier_sha256=file_sha256(contract_verifier)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "test_discovery_guard_sha256=file_sha256(test_discovery_guard)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "solution_sha256=file_sha256(solution)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "schema_identity=\"aggregator-backend/source-verification-proof@1\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceProofUsesBoundedExecutionAndExplicitDiagnosticStatus()
    {
        var source = Read("tools/run-source-verification-proof.py");

        Assert.Contains("--command-timeout-seconds", source, StringComparison.Ordinal);
        Assert.Contains("--allow-dirty", source, StringComparison.Ordinal);
        Assert.Contains("--self-test", source, StringComparison.Ordinal);
        Assert.Contains(
            "release_valid = failure is None and source_identity.tree_clean and not arguments.allow_dirty",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"passed\" if release_valid else \"diagnostic\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceProofKeepsInputsAndEvidenceInsideTheRepository()
    {
        var source = Read("tools/run-source-verification-proof.py");

        Assert.Contains(
            "results_parent = require_repository_path(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "contract_verifier = require_repository_path(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "test_discovery_guard = require_repository_path(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "solution = require_repository_path(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "artifacts/source-verification-proof",
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
