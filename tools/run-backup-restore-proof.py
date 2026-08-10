#!/usr/bin/env python3
"""Bind the canonical backup/restore proof to exact repository evidence.

This command intentionally does not implement backup or restore semantics. It
executes tools/restore-proof.sh as the sole recovery workflow owner, retains its
bounded output and records the exact source/script identities used for proof.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import sys
from dataclasses import dataclass, replace
from datetime import UTC, datetime
from pathlib import Path
from typing import Sequence

from repository_proof_runtime import (
    ProofCommandRecord,
    ProofCommandRunner,
    RepositoryProofError,
    find_repository_root,
    read_source_identity,
    require_bounded_integer,
    require_repository_path,
    restrict_file_permissions,
    write_json_report,
)

MINIMUM_COMMAND_TIMEOUT_SECONDS = 60
MAXIMUM_COMMAND_TIMEOUT_SECONDS = 14_400
MAXIMUM_DELEGATED_ARGUMENTS = 64
MAXIMUM_ARGUMENT_LENGTH = 4_096
MAXIMUM_ARGUMENT_BYTES = 16_384
CANONICAL_SCRIPT = "tools/restore-proof.sh"
CANONICAL_BACKUP_OWNER = "tools/backup.sh"
CANONICAL_RESTORE_OWNER = "tools/restore.sh"


@dataclass(frozen=True)
class BackupRestoreProofReport:
    schema_identity: str
    status: str
    release_valid: bool
    source_commit: str
    source_tree_clean: bool
    allow_dirty: bool
    repository_root: str
    canonical_script: str
    canonical_script_sha256: str
    canonical_backup_owner: str
    canonical_backup_owner_sha256: str
    canonical_restore_owner: str
    canonical_restore_owner_sha256: str
    shell_command: str
    delegated_argument_count: int
    command_timeout_seconds: int
    started_at_utc: str
    finished_at_utc: str
    proof_command: ProofCommandRecord | None
    failure: str | None


def parse_arguments(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Execute the canonical restore-proof.sh workflow and retain exact, "
            "commit-bound recovery evidence."
        )
    )
    parser.add_argument("--repository-root", default=None)
    parser.add_argument("--results-directory", default="artifacts/backup-restore-proof")
    parser.add_argument("--shell-command", default="bash")
    parser.add_argument("--command-timeout-seconds", type=int, default=3_600)
    parser.add_argument("--allow-dirty", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument(
        "proof_arguments",
        nargs=argparse.REMAINDER,
        help="Arguments delegated verbatim to tools/restore-proof.sh after '--'.",
    )
    return parser.parse_args(argv)


def normalize_delegated_arguments(values: Sequence[str]) -> tuple[str, ...]:
    arguments = tuple(values[1:] if values and values[0] == "--" else values)
    if len(arguments) > MAXIMUM_DELEGATED_ARGUMENTS:
        raise RepositoryProofError(
            f"At most {MAXIMUM_DELEGATED_ARGUMENTS} delegated arguments are allowed."
        )

    total_bytes = 0
    for index, value in enumerate(arguments):
        if "\x00" in value:
            raise RepositoryProofError(
                f"Delegated argument {index} contains a forbidden NUL character."
            )
        if len(value) > MAXIMUM_ARGUMENT_LENGTH:
            raise RepositoryProofError(
                f"Delegated argument {index} exceeds {MAXIMUM_ARGUMENT_LENGTH} characters."
            )
        total_bytes += len(value.encode("utf-8"))
    if total_bytes > MAXIMUM_ARGUMENT_BYTES:
        raise RepositoryProofError(
            f"Delegated arguments exceed {MAXIMUM_ARGUMENT_BYTES} UTF-8 bytes."
        )
    return arguments


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def require_executable_file(path: Path, description: str) -> None:
    if not path.is_file():
        raise RepositoryProofError(f"{description} '{path}' does not exist.")
    if path.stat().st_size == 0:
        raise RepositoryProofError(f"{description} '{path}' is empty.")


def resolve_shell(command: str) -> str:
    if command.strip() != command or command == "" or "\x00" in command:
        raise RepositoryProofError("Shell command must be one non-empty executable name or path.")
    candidate = Path(command).expanduser()
    if candidate.is_absolute():
        resolved = candidate.resolve()
        if not resolved.is_file():
            raise RepositoryProofError(f"Shell executable '{resolved}' does not exist.")
        return str(resolved)
    if any(separator in command for separator in ("/", "\\")):
        raise RepositoryProofError(
            "A relative shell command may not contain path separators. Use an absolute path."
        )
    resolved_command = shutil.which(command)
    if resolved_command is None:
        raise RepositoryProofError(f"Shell executable '{command}' was not found on PATH.")
    return resolved_command


def redacted_command(
    shell_command: str,
    repository_root: Path,
    script_path: Path,
    argument_count: int,
) -> tuple[str, ...]:
    redacted = [
        shell_command,
        str(script_path.relative_to(repository_root)),
    ]
    redacted.extend("<delegated-argument-redacted>" for _ in range(argument_count))
    return tuple(redacted)


def run_self_test() -> None:
    if normalize_delegated_arguments(("--", "one", "two")) != ("one", "two"):
        raise RepositoryProofError("Delegated argument separator self-test failed.")
    if normalize_delegated_arguments(("one",)) != ("one",):
        raise RepositoryProofError("Delegated argument normalization self-test failed.")
    try:
        normalize_delegated_arguments(("x\x00y",))
    except RepositoryProofError:
        pass
    else:
        raise RepositoryProofError("NUL argument self-test did not fail closed.")
    try:
        normalize_delegated_arguments(tuple("x" for _ in range(MAXIMUM_DELEGATED_ARGUMENTS + 1)))
    except RepositoryProofError:
        pass
    else:
        raise RepositoryProofError("Argument-count self-test did not fail closed.")
    if len(redacted_command("bash", Path("/repo"), Path("/repo/tools/restore-proof.sh"), 2)) != 4:
        raise RepositoryProofError("Command redaction self-test failed.")
    print("Backup/restore proof self-test passed.")


def execute_proof(arguments: argparse.Namespace) -> Path:
    repository_root = find_repository_root(
        arguments.repository_root,
        "AggregatorBackend.slnx",
    )
    source_identity = read_source_identity(
        repository_root,
        allow_dirty=arguments.allow_dirty,
    )
    results_parent = require_repository_path(
        repository_root,
        arguments.results_directory,
        "Results directory",
    )
    script_path = require_repository_path(
        repository_root,
        CANONICAL_SCRIPT,
        "Canonical restore-proof script",
    )
    backup_owner_path = require_repository_path(
        repository_root,
        CANONICAL_BACKUP_OWNER,
        "Canonical backup owner",
    )
    restore_owner_path = require_repository_path(
        repository_root,
        CANONICAL_RESTORE_OWNER,
        "Canonical restore owner",
    )
    require_executable_file(script_path, "Canonical restore-proof script")
    require_executable_file(backup_owner_path, "Canonical backup owner")
    require_executable_file(restore_owner_path, "Canonical restore owner")

    shell_command = resolve_shell(arguments.shell_command)
    delegated_arguments = normalize_delegated_arguments(arguments.proof_arguments)
    command_timeout_seconds = require_bounded_integer(
        arguments.command_timeout_seconds,
        MINIMUM_COMMAND_TIMEOUT_SECONDS,
        MAXIMUM_COMMAND_TIMEOUT_SECONDS,
        "Command timeout in seconds",
    )

    timestamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%S%fZ")
    results_root = results_parent / timestamp
    results_root.mkdir(parents=True, exist_ok=False)
    restrict_file_permissions(results_root)
    report_path = results_root / "backup-restore-proof.json"
    runner = ProofCommandRunner(
        repository_root,
        results_root,
        command_timeout_seconds,
    )
    started_at = datetime.now(UTC)
    proof_command: ProofCommandRecord | None = None
    failure: str | None = None

    try:
        proof_command, _ = runner.run(
            "canonical-backup-restore-proof",
            [shell_command, str(script_path), *delegated_arguments],
            check=False,
        )
        proof_command = replace(
            proof_command,
            command=redacted_command(
                shell_command,
                repository_root,
                script_path,
                len(delegated_arguments),
            ),
        )
        if proof_command.exit_code != 0:
            reason = (
                "timed out"
                if proof_command.timed_out
                else f"failed with exit code {proof_command.exit_code}"
            )
            failure = (
                f"Canonical restore proof {reason}. "
                f"Inspect '{proof_command.log_path}'."
            )
    except (RepositoryProofError, OSError) as exception:
        failure = str(exception)

    release_valid = failure is None and source_identity.tree_clean and not arguments.allow_dirty
    status = "failed" if failure is not None else ("passed" if release_valid else "diagnostic")
    report = BackupRestoreProofReport(
        schema_identity="aggregator-backend/backup-restore-proof@1",
        status=status,
        release_valid=release_valid,
        source_commit=source_identity.commit_sha,
        source_tree_clean=source_identity.tree_clean,
        allow_dirty=arguments.allow_dirty,
        repository_root=str(repository_root),
        canonical_script=str(script_path.relative_to(repository_root)),
        canonical_script_sha256=file_sha256(script_path),
        canonical_backup_owner=str(backup_owner_path.relative_to(repository_root)),
        canonical_backup_owner_sha256=file_sha256(backup_owner_path),
        canonical_restore_owner=str(restore_owner_path.relative_to(repository_root)),
        canonical_restore_owner_sha256=file_sha256(restore_owner_path),
        shell_command=shell_command,
        delegated_argument_count=len(delegated_arguments),
        command_timeout_seconds=command_timeout_seconds,
        started_at_utc=started_at.isoformat(),
        finished_at_utc=datetime.now(UTC).isoformat(),
        proof_command=proof_command,
        failure=failure,
    )
    write_json_report(report_path, report)
    if failure is not None:
        raise RepositoryProofError(
            f"Backup/restore proof failed: {failure} Report: {report_path}"
        )
    return report_path


def main(argv: Sequence[str] | None = None) -> int:
    arguments = parse_arguments(sys.argv[1:] if argv is None else argv)
    try:
        if arguments.self_test:
            run_self_test()
            return 0
        report_path = execute_proof(arguments)
    except RepositoryProofError as exception:
        print(str(exception), file=sys.stderr)
        return 1
    print(f"Backup/restore proof completed. Report: {report_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
