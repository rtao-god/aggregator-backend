namespace Architecture.Tests;

public sealed class BackupRestoreProofReachabilityTests
{
    [Fact]
    public void BackupRestoreProofDelegatesToTheExistingCanonicalOwners()
    {
        var source = Read("tools/run-backup-restore-proof.py");

        Assert.Contains(
            "CANONICAL_SCRIPT = \"tools/restore-proof.sh\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CANONICAL_BACKUP_OWNER = \"tools/backup.sh\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CANONICAL_RESTORE_OWNER = \"tools/restore.sh\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[shell_command, str(script_path), *delegated_arguments]",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackupRestoreProofDoesNotReimplementBackupOrRestoreSemantics()
    {
        var source = Read("tools/run-backup-restore-proof.py");

        foreach (var forbidden in new[]
                 {
                     "pg_dump",
                     "pg_restore",
                     "createdb",
                     "dropdb",
                     "psql ",
                     "docker exec",
                     "aws s3",
                 })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void BackupRestoreProofBindsEvidenceToExactSourceAndScriptDigests()
    {
        var source = Read("tools/run-backup-restore-proof.py");

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
            "canonical_script_sha256=file_sha256(script_path)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "canonical_backup_owner_sha256=file_sha256(backup_owner_path)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "canonical_restore_owner_sha256=file_sha256(restore_owner_path)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "schema_identity=\"aggregator-backend/backup-restore-proof@1\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackupRestoreProofRedactsDelegatedArgumentsFromEvidence()
    {
        var source = Read("tools/run-backup-restore-proof.py");

        Assert.Contains(
            "<delegated-argument-redacted>",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "proof_command = replace(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "delegated_argument_count=len(delegated_arguments)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "delegated_arguments=delegated_arguments",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MAXIMUM_DELEGATED_ARGUMENTS = 64",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MAXIMUM_ARGUMENT_BYTES = 16_384",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackupRestoreProofUsesBoundedExecutionAndDiagnosticStatus()
    {
        var source = Read("tools/run-backup-restore-proof.py");

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
        Assert.Contains(
            "check=False",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if proof_command.exit_code != 0:",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackupRestoreProofKeepsAllArtifactsInsideTheRepository()
    {
        var source = Read("tools/run-backup-restore-proof.py");

        Assert.Contains(
            "results_parent = require_repository_path(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "script_path = require_repository_path(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "backup_owner_path = require_repository_path(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "restore_owner_path = require_repository_path(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "artifacts/backup-restore-proof",
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
