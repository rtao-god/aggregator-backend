#!/usr/bin/env python3
"""Verify an explicit, same-commit set of repository release proofs.

The verifier never discovers or selects a "latest" artifact. Every proof path is
provided explicitly, validated against its schema and exact command-log digests,
and bound to the currently checked-out clean commit.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import tempfile
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

from repository_proof_runtime import (
    RepositoryProofError,
    find_repository_root,
    read_source_identity,
    require_repository_path,
    restrict_file_permissions,
    write_json_report,
)

SCHEMAS: Mapping[str, str] = {
    "source": "aggregator-backend/source-verification-proof@1",
    "migration": "aggregator-backend/migration-proof@1",
    "runtime": "aggregator-backend/runtime-smoke-proof@1",
    "backup_restore": "aggregator-backend/backup-restore-proof@1",
}
CANONICAL_CONTEXTS: tuple[str, ...] = (
    "catalog",
    "query",
    "ingestion",
    "analytics",
    "promotion",
)
CANONICAL_RUNTIME_SERVICES: tuple[str, ...] = (
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
)
CANONICAL_API_SERVICES: frozenset[str] = frozenset(
    {
        "catalog-api",
        "query-api",
        "ingestion-api",
        "analytics-api",
        "promotion-api",
    }
)
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")


@dataclass(frozen=True)
class ReleaseEvidenceInput:
    kind: str
    path: str
    sha256: str
    schema_identity: str


@dataclass(frozen=True)
class ReleaseEvidenceIndex:
    schema_identity: str
    status: str
    release_valid: bool
    source_commit: str
    source_tree_clean: bool
    created_at_utc: str
    proofs: tuple[ReleaseEvidenceInput, ...]


def parse_arguments(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Verify explicit source, migration, runtime and backup/restore proofs."
    )
    parser.add_argument("--repository-root", default=None)
    parser.add_argument("--source-report")
    parser.add_argument("--migration-report")
    parser.add_argument("--runtime-smoke-report")
    parser.add_argument("--backup-restore-report")
    parser.add_argument("--results-directory", default="artifacts/release-evidence")
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args(argv)


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_json_object(path: Path, description: str) -> Mapping[str, Any]:
    if not path.is_file():
        raise RepositoryProofError(f"{description} '{path}' does not exist.")
    if path.stat().st_size == 0:
        raise RepositoryProofError(f"{description} '{path}' is empty.")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exception:
        raise RepositoryProofError(
            f"{description} '{path}' is not valid UTF-8 JSON: {exception}"
        ) from exception
    if not isinstance(value, dict):
        raise RepositoryProofError(f"{description} '{path}' must contain a JSON object.")
    return value


def require_text(value: Any, field: str) -> str:
    if not isinstance(value, str) or value == "" or value.strip() != value:
        raise RepositoryProofError(f"Release evidence field '{field}' must be non-empty text.")
    return value


def require_sha256(value: Any, field: str) -> str:
    normalized = require_text(value, field).lower()
    if not SHA256_PATTERN.fullmatch(normalized):
        raise RepositoryProofError(
            f"Release evidence field '{field}' must be a SHA-256 hexadecimal digest."
        )
    return normalized


def require_common_proof(
    kind: str,
    report: Mapping[str, Any],
    expected_commit: str,
) -> None:
    expected_schema = SCHEMAS[kind]
    if report.get("schema_identity") != expected_schema:
        raise RepositoryProofError(
            f"{kind} proof schema mismatch: expected '{expected_schema}', "
            f"received '{report.get('schema_identity')}'."
        )
    if report.get("status") != "passed":
        raise RepositoryProofError(
            f"{kind} proof status must be 'passed', received '{report.get('status')}'."
        )
    if report.get("release_valid") is not True:
        raise RepositoryProofError(f"{kind} proof is not release-valid.")
    if report.get("source_tree_clean") is not True:
        raise RepositoryProofError(f"{kind} proof was not produced from a clean tree.")
    if report.get("allow_dirty") is not False:
        raise RepositoryProofError(f"{kind} proof used a dirty-tree diagnostic override.")
    if report.get("source_commit") != expected_commit:
        raise RepositoryProofError(
            f"{kind} proof belongs to commit '{report.get('source_commit')}', "
            f"not '{expected_commit}'."
        )
    if report.get("failure") is not None:
        raise RepositoryProofError(f"{kind} proof retains a failure value.")


def validate_command_record(
    repository_root: Path,
    command: Any,
    field: str,
    *,
    required: bool = True,
) -> None:
    if command is None:
        if required:
            raise RepositoryProofError(f"Release evidence command '{field}' is missing.")
        return
    if not isinstance(command, dict):
        raise RepositoryProofError(f"Release evidence command '{field}' must be an object.")
    if command.get("exit_code") != 0:
        raise RepositoryProofError(
            f"Release evidence command '{field}' has exit code '{command.get('exit_code')}'."
        )
    if command.get("timed_out") is not False:
        raise RepositoryProofError(f"Release evidence command '{field}' timed out.")
    log_path_text = require_text(command.get("log_path"), f"{field}.log_path")
    log_path = require_repository_path(
        repository_root,
        log_path_text,
        f"{field} log",
    )
    if not log_path.is_file():
        raise RepositoryProofError(
            f"Release evidence command '{field}' log '{log_path}' is missing."
        )
    expected_log_digest = require_sha256(
        command.get("log_sha256"),
        f"{field}.log_sha256",
    )
    actual_log_digest = file_sha256(log_path)
    if actual_log_digest != expected_log_digest:
        raise RepositoryProofError(
            f"Release evidence command '{field}' log digest mismatch."
        )


def validate_current_file_digest(
    repository_root: Path,
    report: Mapping[str, Any],
    path_field: str,
    digest_field: str,
) -> None:
    relative_path = require_text(report.get(path_field), path_field)
    path = require_repository_path(repository_root, relative_path, path_field)
    if not path.is_file():
        raise RepositoryProofError(f"Release owner file '{path}' is missing.")
    expected = require_sha256(report.get(digest_field), digest_field)
    if file_sha256(path) != expected:
        raise RepositoryProofError(
            f"Release owner file '{relative_path}' no longer matches '{digest_field}'."
        )


def validate_source_proof(
    repository_root: Path,
    report: Mapping[str, Any],
) -> None:
    validate_command_record(repository_root, report.get("contract_command"), "source.contract")
    validate_command_record(repository_root, report.get("build_command"), "source.build")
    validate_command_record(repository_root, report.get("test_command"), "source.tests")
    validate_current_file_digest(
        repository_root,
        report,
        "contract_verifier",
        "contract_verifier_sha256",
    )
    validate_current_file_digest(
        repository_root,
        report,
        "test_discovery_guard",
        "test_discovery_guard_sha256",
    )
    validate_current_file_digest(
        repository_root,
        report,
        "solution",
        "solution_sha256",
    )


def validate_migration_proof(
    repository_root: Path,
    report: Mapping[str, Any],
) -> None:
    contexts = report.get("contexts")
    if contexts != list(CANONICAL_CONTEXTS):
        raise RepositoryProofError(
            "Migration proof must cover every canonical context in canonical order."
        )
    validate_command_record(
        repository_root,
        report.get("configuration_command"),
        "migration.configuration",
    )
    validate_command_record(
        repository_root,
        report.get("dependency_start_command"),
        "migration.dependencies",
        required=False,
    )
    validate_command_record(
        repository_root,
        report.get("cleanup_command"),
        "migration.cleanup",
    )
    passes = report.get("migration_passes")
    if not isinstance(passes, list) or len(passes) != len(CANONICAL_CONTEXTS) * 2:
        raise RepositoryProofError("Migration proof must contain exactly two passes per context.")
    observed: set[tuple[str, int]] = set()
    for index, item in enumerate(passes):
        if not isinstance(item, dict):
            raise RepositoryProofError(f"Migration pass {index} must be an object.")
        context = item.get("context")
        pass_number = item.get("pass_number")
        service = item.get("service")
        if context not in CANONICAL_CONTEXTS or pass_number not in (1, 2):
            raise RepositoryProofError(f"Migration pass {index} has invalid identity.")
        if service != f"{context}-migrate":
            raise RepositoryProofError(f"Migration pass {index} has invalid owner service.")
        identity = (str(context), int(pass_number))
        if identity in observed:
            raise RepositoryProofError(f"Migration pass identity {identity} is duplicated.")
        observed.add(identity)
        validate_command_record(
            repository_root,
            item.get("command"),
            f"migration.{context}.pass{pass_number}",
        )
    expected = {(context, pass_number) for context in CANONICAL_CONTEXTS for pass_number in (1, 2)}
    if observed != expected:
        raise RepositoryProofError("Migration proof pass set is incomplete.")


def validate_runtime_proof(
    repository_root: Path,
    report: Mapping[str, Any],
) -> None:
    for field, required in (
        ("configuration_command", True),
        ("resolved_images_command", True),
        ("dependency_start_command", False),
        ("runtime_start_command", True),
        ("runtime_state_command", True),
        ("diagnostic_logs_command", False),
        ("cleanup_command", True),
    ):
        validate_command_record(
            repository_root,
            report.get(field),
            f"runtime.{field}",
            required=required,
        )

    migration_commands = report.get("migration_commands")
    if not isinstance(migration_commands, list) or len(migration_commands) != 5:
        raise RepositoryProofError("Runtime proof must execute exactly five migration commands.")
    for index, command in enumerate(migration_commands):
        validate_command_record(
            repository_root,
            command,
            f"runtime.migration{index + 1}",
        )

    resolved_images = report.get("resolved_images")
    if not isinstance(resolved_images, list) or len(resolved_images) == 0:
        raise RepositoryProofError("Runtime proof has no resolved image evidence.")
    for index, image in enumerate(resolved_images):
        value = require_text(image, f"runtime.resolved_images[{index}]").lower()
        final_segment = value.rsplit("/", 1)[-1]
        if "@sha256:" not in value and (
            ":" not in final_segment or final_segment.endswith(":latest")
        ):
            raise RepositoryProofError(f"Runtime image '{image}' is not pinned.")

    evidence = report.get("runtime_evidence")
    if not isinstance(evidence, list) or len(evidence) != len(CANONICAL_RUNTIME_SERVICES):
        raise RepositoryProofError("Runtime proof does not cover every canonical deployable.")
    by_service: dict[str, Mapping[str, Any]] = {}
    for item in evidence:
        if not isinstance(item, dict):
            raise RepositoryProofError("Runtime container evidence must be an object.")
        service = require_text(item.get("service"), "runtime.service")
        if service in by_service:
            raise RepositoryProofError(f"Runtime service '{service}' is duplicated.")
        by_service[service] = item
    if tuple(sorted(by_service)) != tuple(sorted(CANONICAL_RUNTIME_SERVICES)):
        raise RepositoryProofError("Runtime service evidence set is incomplete or foreign.")
    for service in CANONICAL_RUNTIME_SERVICES:
        item = by_service[service]
        if item.get("state") != "running":
            raise RepositoryProofError(f"Runtime service '{service}' was not running.")
        require_text(item.get("container_id"), f"runtime.{service}.container_id")
        require_text(item.get("image"), f"runtime.{service}.image")
        if service in CANONICAL_API_SERVICES and item.get("health") != "healthy":
            raise RepositoryProofError(f"Runtime API service '{service}' was not healthy.")


def validate_backup_restore_proof(
    repository_root: Path,
    report: Mapping[str, Any],
) -> None:
    validate_command_record(
        repository_root,
        report.get("proof_command"),
        "backup_restore.proof",
    )
    validate_current_file_digest(
        repository_root,
        report,
        "canonical_script",
        "canonical_script_sha256",
    )
    validate_current_file_digest(
        repository_root,
        report,
        "canonical_backup_owner",
        "canonical_backup_owner_sha256",
    )
    validate_current_file_digest(
        repository_root,
        report,
        "canonical_restore_owner",
        "canonical_restore_owner_sha256",
    )
    command = report.get("proof_command")
    assert isinstance(command, dict)
    command_arguments = command.get("command")
    if not isinstance(command_arguments, list) or len(command_arguments) < 2:
        raise RepositoryProofError("Backup/restore proof command evidence is invalid.")
    if command_arguments[1] != "tools/restore-proof.sh":
        raise RepositoryProofError("Backup/restore proof did not use the canonical script.")
    if any(
        value != "<delegated-argument-redacted>"
        for value in command_arguments[2:]
    ):
        raise RepositoryProofError(
            "Backup/restore proof persisted an unredacted delegated argument."
        )
    if report.get("delegated_argument_count") != len(command_arguments) - 2:
        raise RepositoryProofError(
            "Backup/restore delegated argument count does not match redacted evidence."
        )


def explicit_report_path(
    repository_root: Path,
    value: str | None,
    description: str,
) -> Path:
    if value is None:
        raise RepositoryProofError(f"Explicit {description} path is required.")
    return require_repository_path(repository_root, value, description)


def build_input(
    repository_root: Path,
    kind: str,
    path: Path,
    report: Mapping[str, Any],
) -> ReleaseEvidenceInput:
    return ReleaseEvidenceInput(
        kind=kind,
        path=str(path.relative_to(repository_root)),
        sha256=file_sha256(path),
        schema_identity=require_text(report.get("schema_identity"), f"{kind}.schema_identity"),
    )


def run_self_test() -> None:
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        log = root / "command.log"
        log.write_text("proof\n", encoding="utf-8")
        command = {
            "exit_code": 0,
            "timed_out": False,
            "log_path": "command.log",
            "log_sha256": file_sha256(log),
        }
        validate_command_record(root, command, "self-test")
        corrupt = dict(command)
        corrupt["log_sha256"] = "0" * 64
        try:
            validate_command_record(root, corrupt, "self-test-corrupt")
        except RepositoryProofError:
            pass
        else:
            raise RepositoryProofError("Log corruption self-test did not fail closed.")

        common = {
            "schema_identity": SCHEMAS["source"],
            "status": "passed",
            "release_valid": True,
            "source_tree_clean": True,
            "allow_dirty": False,
            "source_commit": "a" * 40,
            "failure": None,
        }
        require_common_proof("source", common, "a" * 40)
        diagnostic = dict(common)
        diagnostic["release_valid"] = False
        try:
            require_common_proof("source", diagnostic, "a" * 40)
        except RepositoryProofError:
            pass
        else:
            raise RepositoryProofError("Diagnostic proof self-test did not fail closed.")
        mixed = dict(common)
        mixed["source_commit"] = "b" * 40
        try:
            require_common_proof("source", mixed, "a" * 40)
        except RepositoryProofError:
            pass
        else:
            raise RepositoryProofError("Mixed-commit self-test did not fail closed.")
    print("Release evidence verifier self-test passed.")


def execute_verification(arguments: argparse.Namespace) -> Path:
    repository_root = find_repository_root(
        arguments.repository_root,
        "AggregatorBackend.slnx",
    )
    source_identity = read_source_identity(repository_root, allow_dirty=False)
    report_paths = {
        "source": explicit_report_path(
            repository_root,
            arguments.source_report,
            "source verification report",
        ),
        "migration": explicit_report_path(
            repository_root,
            arguments.migration_report,
            "migration proof report",
        ),
        "runtime": explicit_report_path(
            repository_root,
            arguments.runtime_smoke_report,
            "runtime smoke report",
        ),
        "backup_restore": explicit_report_path(
            repository_root,
            arguments.backup_restore_report,
            "backup/restore proof report",
        ),
    }
    reports = {
        kind: load_json_object(path, f"{kind} proof")
        for kind, path in report_paths.items()
    }
    for kind, report in reports.items():
        require_common_proof(kind, report, source_identity.commit_sha)

    validate_source_proof(repository_root, reports["source"])
    validate_migration_proof(repository_root, reports["migration"])
    validate_runtime_proof(repository_root, reports["runtime"])
    validate_backup_restore_proof(repository_root, reports["backup_restore"])

    results_parent = require_repository_path(
        repository_root,
        arguments.results_directory,
        "Results directory",
    )
    timestamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%S%fZ")
    results_root = results_parent / timestamp
    results_root.mkdir(parents=True, exist_ok=False)
    restrict_file_permissions(results_root)
    index_path = results_root / "release-evidence.json"
    proof_inputs = tuple(
        build_input(repository_root, kind, report_paths[kind], reports[kind])
        for kind in ("source", "migration", "runtime", "backup_restore")
    )
    index = ReleaseEvidenceIndex(
        schema_identity="aggregator-backend/release-evidence-index@1",
        status="passed",
        release_valid=True,
        source_commit=source_identity.commit_sha,
        source_tree_clean=source_identity.tree_clean,
        created_at_utc=datetime.now(UTC).isoformat(),
        proofs=proof_inputs,
    )
    write_json_report(index_path, index)
    return index_path


def main(argv: Sequence[str] | None = None) -> int:
    arguments = parse_arguments(sys.argv[1:] if argv is None else argv)
    try:
        if arguments.self_test:
            run_self_test()
            return 0
        index_path = execute_verification(arguments)
    except RepositoryProofError as exception:
        print(str(exception), file=sys.stderr)
        return 1
    print(f"Release evidence verified. Index: {index_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
