namespace Architecture.Tests;

public sealed class ReleaseEvidenceVerifierReachabilityTests
{
    [Fact]
    public void ReleaseEvidenceRequiresEveryProofPathExplicitly()
    {
        var source = Read("tools/verify-release-evidence.py");

        foreach (var argument in new[]
                 {
                     "--source-report",
                     "--migration-report",
                     "--runtime-smoke-report",
                     "--backup-restore-report",
                 })
        {
            Assert.Contains(argument, source, StringComparison.Ordinal);
        }

        Assert.Contains(
            "Explicit {description} path is required.",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "explicit_report_path(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseEvidenceNeverDiscoversLatestProofArtifacts()
    {
        var source = Read("tools/verify-release-evidence.py");

        foreach (var forbidden in new[]
                 {
                     ".glob(",
                     ".rglob(",
                     "Path.glob",
                     "Path.rglob",
                     "latest",
                     "newest",
                     "most recent",
                 })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ReleaseEvidenceRequiresCanonicalSchemasAndSameCleanCommit()
    {
        var source = Read("tools/verify-release-evidence.py");

        foreach (var schema in new[]
                 {
                     "aggregator-backend/source-verification-proof@1",
                     "aggregator-backend/migration-proof@1",
                     "aggregator-backend/runtime-smoke-proof@1",
                     "aggregator-backend/backup-restore-proof@1",
                 })
        {
            Assert.Contains(schema, source, StringComparison.Ordinal);
        }

        Assert.Contains(
            "source_identity = read_source_identity(repository_root, allow_dirty=False)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "report.get(\"source_commit\") != expected_commit",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "report.get(\"source_tree_clean\") is not True",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "report.get(\"allow_dirty\") is not False",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "report.get(\"release_valid\") is not True",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseEvidenceRehashesEveryReferencedCommandLog()
    {
        var source = Read("tools/verify-release-evidence.py");

        Assert.Contains(
            "validate_command_record(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "actual_log_digest = file_sha256(log_path)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "actual_log_digest != expected_log_digest",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "command.get(\"exit_code\") != 0",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "command.get(\"timed_out\") is not False",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "log_path = require_repository_path(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseEvidenceRequiresCompleteMigrationAndRuntimeSets()
    {
        var source = Read("tools/verify-release-evidence.py");

        Assert.Contains(
            "len(CANONICAL_CONTEXTS) * 2",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "expected = {(context, pass_number)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "len(migration_commands) != 5",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "len(CANONICAL_RUNTIME_SERVICES)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "item.get(\"state\") != \"running\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "item.get(\"health\") != \"healthy\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseEvidenceRevalidatesCurrentOwnerFilesAndBackupRedaction()
    {
        var source = Read("tools/verify-release-evidence.py");

        Assert.Contains(
            "validate_current_file_digest(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "contract_verifier_sha256",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "test_discovery_guard_sha256",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "canonical_script_sha256",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "canonical_backup_owner_sha256",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "canonical_restore_owner_sha256",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "<delegated-argument-redacted>",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "persisted an unredacted delegated argument",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseEvidenceWritesOneExactCommitBoundIndex()
    {
        var source = Read("tools/verify-release-evidence.py");

        Assert.Contains(
            "schema_identity=\"aggregator-backend/release-evidence-index@1\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "release_valid=True",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "source_commit=source_identity.commit_sha",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "proofs=proof_inputs",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "sha256=file_sha256(path)",
            source,
            StringComparison.Ordinal);
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
