#!/usr/bin/env python3
"""Produce commit-bound proof for contracts, compilation and guarded tests."""

from __future__ import annotations

import argparse
import hashlib
import sys
from dataclasses import dataclass
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

CONTRACT_VERIFIER = "tools/verify-contracts.py"
TEST_DISCOVERY_GUARD = "tools/run-tests-with-discovery-guard.py"
SOLUTION = "AggregatorBackend.slnx"
MINIMUM_COMMAND_TIMEOUT_SECONDS = 60
MAXIMUM_COMMAND_TIMEOUT_SECONDS = 7_200


@dataclass(frozen=True)
class SourceVerificationProofReport:
    schema_identity: str
    status: str
    release_valid: bool
    source_commit: str
    source_tree_clean: bool
    allow_dirty: bool
    repository_root: str
    contract_verifier: str
    contract_verifier_sha256: str
    test_discovery_guard: str
    test_discovery_guard_sha256: str
    solution: str
    solution_sha256: str
    python_executable: str
    command_timeout_seconds: int
    started_at_utc: str
    finished_at_utc: str
    contract_command: ProofCommandRecord | None
    build_command: ProofCommandRecord | None
    test_command: ProofCommandRecord | None
    failure: str | None


def parse_arguments(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Verify contracts, compile the solution and run guarded tests."
    )
    parser.add_argument("--repository-root", default=None)
    parser.add_argument("--results-directory", default="artifacts/source-verification-proof")
    parser.add_argument("--command-timeout-seconds", type=int, default=1_800)
    parser.add_argument("--allow-dirty", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args(argv)


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def require_nonempty_file(path: Path, description: str) -> None:
    if not path.is_file():
        raise RepositoryProofError(f"{description} '{path}' does not exist.")
    if path.stat().st_size == 0:
        raise RepositoryProofError(f"{description} '{path}' is empty.")


def run_self_test() -> None:
    if require_bounded_integer(60, 60, 120, "self-test") != 60:
        raise RepositoryProofError("Timeout bound self-test failed.")
    if not CONTRACT_VERIFIER.endswith("verify-contracts.py"):
        raise RepositoryProofError("Contract owner self-test failed.")
    if not TEST_DISCOVERY_GUARD.endswith("run-tests-with-discovery-guard.py"):
        raise RepositoryProofError("Test guard owner self-test failed.")
    if not SOLUTION.endswith(".slnx"):
        raise RepositoryProofError("Solution identity self-test failed.")
    print("Source verification proof self-test passed.")


def execute_proof(arguments: argparse.Namespace) -> Path:
    repository_root = find_repository_root(arguments.repository_root, SOLUTION)
    source_identity = read_source_identity(
        repository_root,
        allow_dirty=arguments.allow_dirty,
    )
    results_parent = require_repository_path(
        repository_root,
        arguments.results_directory,
        "Results directory",
    )
    contract_verifier = require_repository_path(
        repository_root,
        CONTRACT_VERIFIER,
        "Contract verifier",
    )
    test_discovery_guard = require_repository_path(
        repository_root,
        TEST_DISCOVERY_GUARD,
        "Test-discovery guard",
    )
    solution = require_repository_path(repository_root, SOLUTION, "Solution")
    require_nonempty_file(contract_verifier, "Contract verifier")
    require_nonempty_file(test_discovery_guard, "Test-discovery guard")
    require_nonempty_file(solution, "Solution")

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
    report_path = results_root / "source-verification-proof.json"
    runner = ProofCommandRunner(
        repository_root,
        results_root,
        command_timeout_seconds,
    )
    started_at = datetime.now(UTC)
    contract_command: ProofCommandRecord | None = None
    build_command: ProofCommandRecord | None = None
    test_command: ProofCommandRecord | None = None
    failure: str | None = None

    try:
        contract_command, _ = runner.run(
            "verify-contracts",
            [sys.executable, str(contract_verifier)],
        )
        build_command, _ = runner.run(
            "build-solution",
            [
                "dotnet",
                "build",
                str(solution),
                "/m:1",
                "/nr:false",
                "--nologo",
            ],
        )
        test_command, _ = runner.run(
            "run-guarded-tests",
            [sys.executable, str(test_discovery_guard)],
        )
    except (RepositoryProofError, OSError) as exception:
        failure = str(exception)

    release_valid = failure is None and source_identity.tree_clean and not arguments.allow_dirty
    status = "failed" if failure is not None else ("passed" if release_valid else "diagnostic")
    report = SourceVerificationProofReport(
        schema_identity="aggregator-backend/source-verification-proof@1",
        status=status,
        release_valid=release_valid,
        source_commit=source_identity.commit_sha,
        source_tree_clean=source_identity.tree_clean,
        allow_dirty=arguments.allow_dirty,
        repository_root=str(repository_root),
        contract_verifier=str(contract_verifier.relative_to(repository_root)),
        contract_verifier_sha256=file_sha256(contract_verifier),
        test_discovery_guard=str(test_discovery_guard.relative_to(repository_root)),
        test_discovery_guard_sha256=file_sha256(test_discovery_guard),
        solution=str(solution.relative_to(repository_root)),
        solution_sha256=file_sha256(solution),
        python_executable=sys.executable,
        command_timeout_seconds=command_timeout_seconds,
        started_at_utc=started_at.isoformat(),
        finished_at_utc=datetime.now(UTC).isoformat(),
        contract_command=contract_command,
        build_command=build_command,
        test_command=test_command,
        failure=failure,
    )
    write_json_report(report_path, report)
    if failure is not None:
        raise RepositoryProofError(
            f"Source verification proof failed: {failure} Report: {report_path}"
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
    print(f"Source verification proof completed. Report: {report_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
