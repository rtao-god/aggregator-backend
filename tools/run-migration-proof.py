#!/usr/bin/env python3
"""Run every bounded-context migration deployable in an isolated Compose project.

The proof is deliberately outside API/worker startup. It validates the production
migration composition roots, executes each one-shot owner twice, retains exact
command logs and SHA-256 digests, and tears down only the uniquely named proof
project and its volumes.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

CONTEXTS: tuple[str, ...] = (
    "catalog",
    "query",
    "ingestion",
    "analytics",
    "promotion",
)
MIGRATION_SERVICE_BY_CONTEXT: Mapping[str, str] = {
    context: f"{context}-migrate" for context in CONTEXTS
}
PROJECT_NAME_PATTERN = re.compile(r"^[a-z0-9][a-z0-9_-]*$")


class MigrationProofError(RuntimeError):
    """Typed CLI failure that must terminate the proof with a non-zero exit."""


@dataclass(frozen=True)
class CommandRecord:
    purpose: str
    command: tuple[str, ...]
    started_at_utc: str
    finished_at_utc: str
    duration_seconds: float
    exit_code: int
    log_path: str
    log_sha256: str


@dataclass(frozen=True)
class MigrationPassRecord:
    context: str
    service: str
    pass_number: int
    command: CommandRecord


@dataclass(frozen=True)
class MigrationProofReport:
    schema_identity: str
    status: str
    repository_root: str
    compose_file: str
    environment_file: str
    compose_project_name: str
    started_at_utc: str
    finished_at_utc: str
    contexts: tuple[str, ...]
    dependency_services: tuple[str, ...]
    configuration_command: CommandRecord | None
    dependency_start_command: CommandRecord | None
    migration_passes: tuple[MigrationPassRecord, ...]
    cleanup_command: CommandRecord | None
    failure: str | None


class CommandRunner:
    def __init__(self, repository_root: Path, results_directory: Path) -> None:
        self._repository_root = repository_root
        self._results_directory = results_directory
        self._sequence = 0

    def run(
        self,
        purpose: str,
        command: Sequence[str],
        *,
        check: bool = True,
        environment: Mapping[str, str] | None = None,
    ) -> tuple[CommandRecord, str]:
        self._sequence += 1
        started = datetime.now(UTC)
        started_monotonic = time.monotonic()
        completed = subprocess.run(
            list(command),
            cwd=self._repository_root,
            env=dict(environment) if environment is not None else None,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
        )
        finished = datetime.now(UTC)
        output = completed.stdout or ""
        safe_purpose = re.sub(r"[^a-z0-9]+", "-", purpose.lower()).strip("-")
        log_name = f"{self._sequence:02d}-{safe_purpose or 'command'}.log"
        log_path = self._results_directory / log_name
        log_path.write_text(output, encoding="utf-8")
        record = CommandRecord(
            purpose=purpose,
            command=tuple(command),
            started_at_utc=started.isoformat(),
            finished_at_utc=finished.isoformat(),
            duration_seconds=round(time.monotonic() - started_monotonic, 6),
            exit_code=completed.returncode,
            log_path=str(log_path.relative_to(self._repository_root)),
            log_sha256=hashlib.sha256(output.encode("utf-8")).hexdigest(),
        )
        if check and completed.returncode != 0:
            raise MigrationProofError(
                f"Command '{purpose}' failed with exit code {completed.returncode}. "
                f"Inspect '{record.log_path}'."
            )
        return record, output


def parse_arguments(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run isolated, repeatable migration proof for all backend database owners."
    )
    parser.add_argument("--repository-root", default=None)
    parser.add_argument("--compose-file", default="compose.yaml")
    parser.add_argument("--env-file", default=".env")
    parser.add_argument("--results-directory", default="artifacts/migration-proof")
    parser.add_argument(
        "--contexts",
        nargs="+",
        default=list(CONTEXTS),
        help="Subset of canonical contexts to prove.",
    )
    parser.add_argument(
        "--keep-project",
        action="store_true",
        help="Keep the isolated Compose project for diagnosis instead of cleaning it up.",
    )
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args(argv)


def find_repository_root(explicit: str | None) -> Path:
    if explicit is not None:
        candidate = Path(explicit).expanduser().resolve()
        if not (candidate / "AggregatorBackend.slnx").is_file():
            raise MigrationProofError(
                f"Repository root '{candidate}' does not contain AggregatorBackend.slnx."
            )
        return candidate

    candidate = Path(__file__).resolve().parent
    while candidate != candidate.parent:
        if (candidate / "AggregatorBackend.slnx").is_file():
            return candidate
        candidate = candidate.parent
    raise MigrationProofError("Could not locate the aggregator-backend repository root.")


def normalize_contexts(values: Iterable[str]) -> tuple[str, ...]:
    normalized: list[str] = []
    for value in values:
        context = value.strip().lower()
        if context not in MIGRATION_SERVICE_BY_CONTEXT:
            allowed = ", ".join(CONTEXTS)
            raise MigrationProofError(
                f"Unknown migration context '{value}'. Allowed values: {allowed}."
            )
        if context not in normalized:
            normalized.append(context)
    if not normalized:
        raise MigrationProofError("At least one migration context is required.")
    return tuple(normalized)


def make_project_name() -> str:
    timestamp = datetime.now(UTC).strftime("%Y%m%d%H%M%S")
    project_name = f"aggregator-migration-proof-{timestamp}-{os.getpid()}".lower()
    if not PROJECT_NAME_PATTERN.fullmatch(project_name):
        raise MigrationProofError(
            f"Generated Compose project name '{project_name}' is invalid."
        )
    return project_name


def compose_prefix(
    compose_file: Path,
    environment_file: Path,
    project_name: str,
) -> list[str]:
    return [
        "docker",
        "compose",
        "--project-name",
        project_name,
        "--file",
        str(compose_file),
        "--env-file",
        str(environment_file),
    ]


def parse_compose_configuration(output: str) -> Mapping[str, Any]:
    try:
        parsed = json.loads(output)
    except json.JSONDecodeError as exception:
        raise MigrationProofError(
            "Docker Compose did not return valid JSON configuration. "
            "A Compose version supporting 'config --format json' is required."
        ) from exception
    if not isinstance(parsed, dict) or not isinstance(parsed.get("services"), dict):
        raise MigrationProofError("Docker Compose configuration has no services object.")
    return parsed


def service_dependencies(service: Mapping[str, Any]) -> tuple[str, ...]:
    depends_on = service.get("depends_on", {})
    if isinstance(depends_on, dict):
        return tuple(str(name) for name in depends_on)
    if isinstance(depends_on, list):
        return tuple(str(name) for name in depends_on)
    raise MigrationProofError("Compose service depends_on must be an object or an array.")


def dependency_closure(
    configuration: Mapping[str, Any],
    roots: Iterable[str],
) -> tuple[str, ...]:
    services = configuration["services"]
    assert isinstance(services, dict)
    pending = list(roots)
    visited: set[str] = set()
    while pending:
        service_name = pending.pop()
        if service_name in visited:
            continue
        service = services.get(service_name)
        if not isinstance(service, dict):
            raise MigrationProofError(
                f"Compose service '{service_name}' required by migration proof is missing."
            )
        visited.add(service_name)
        pending.extend(service_dependencies(service))
    return tuple(sorted(visited))


def ensure_migration_services(
    configuration: Mapping[str, Any],
    contexts: Iterable[str],
) -> tuple[str, ...]:
    services = configuration["services"]
    assert isinstance(services, dict)
    migration_services = tuple(MIGRATION_SERVICE_BY_CONTEXT[context] for context in contexts)
    missing = [name for name in migration_services if name not in services]
    if missing:
        raise MigrationProofError(
            "Compose is missing migration owner service(s): " + ", ".join(missing)
        )
    return migration_services


def write_report(path: Path, report: MigrationProofReport) -> None:
    payload = asdict(report)
    path.write_text(
        json.dumps(payload, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )


def run_self_test() -> None:
    synthetic = {
        "services": {
            "postgres": {},
            "database-init": {"depends_on": {"postgres": {"condition": "service_healthy"}}},
            "catalog-migrate": {"depends_on": {"database-init": {}}},
            "query-migrate": {"depends_on": ["database-init"]},
        }
    }
    closure = dependency_closure(synthetic, ("catalog-migrate", "query-migrate"))
    expected = ("catalog-migrate", "database-init", "postgres", "query-migrate")
    if closure != expected:
        raise MigrationProofError(
            f"Dependency closure self-test failed: expected {expected}, received {closure}."
        )
    if normalize_contexts(("Catalog", "catalog", "Query")) != ("catalog", "query"):
        raise MigrationProofError("Context normalization self-test failed.")
    try:
        normalize_contexts(("unknown",))
    except MigrationProofError:
        pass
    else:
        raise MigrationProofError("Unknown context self-test did not fail closed.")
    parsed = parse_compose_configuration(json.dumps(synthetic))
    if parsed != synthetic:
        raise MigrationProofError("Compose JSON parsing self-test failed.")
    print("Migration proof self-test passed.")


def execute_proof(arguments: argparse.Namespace) -> Path:
    repository_root = find_repository_root(arguments.repository_root)
    compose_file = (repository_root / arguments.compose_file).resolve()
    environment_file = (repository_root / arguments.env_file).resolve()
    if not compose_file.is_file():
        raise MigrationProofError(f"Compose file '{compose_file}' does not exist.")
    if not environment_file.is_file():
        raise MigrationProofError(
            f"Environment file '{environment_file}' does not exist. "
            "Create it explicitly from .env.example and provide required secrets."
        )

    contexts = normalize_contexts(arguments.contexts)
    project_name = make_project_name()
    timestamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    results_root = (repository_root / arguments.results_directory / timestamp).resolve()
    results_root.mkdir(parents=True, exist_ok=False)
    report_path = results_root / "migration-proof.json"
    runner = CommandRunner(repository_root, results_root)
    prefix = compose_prefix(compose_file, environment_file, project_name)
    started_at = datetime.now(UTC)

    configuration_record: CommandRecord | None = None
    dependency_start_record: CommandRecord | None = None
    cleanup_record: CommandRecord | None = None
    migration_passes: list[MigrationPassRecord] = []
    dependency_services: tuple[str, ...] = ()
    failure: str | None = None

    try:
        configuration_record, configuration_output = runner.run(
            "compose-configuration",
            [*prefix, "config", "--format", "json"],
        )
        configuration = parse_compose_configuration(configuration_output)
        migration_services = ensure_migration_services(configuration, contexts)
        closure = dependency_closure(configuration, migration_services)
        dependency_services = tuple(
            service for service in closure if service not in migration_services
        )
        if dependency_services:
            dependency_start_record, _ = runner.run(
                "start-migration-dependencies",
                [
                    *prefix,
                    "up",
                    "--detach",
                    "--wait",
                    "--remove-orphans",
                    *dependency_services,
                ],
            )

        for context in contexts:
            service = MIGRATION_SERVICE_BY_CONTEXT[context]
            for pass_number in (1, 2):
                record, _ = runner.run(
                    f"{context}-migration-pass-{pass_number}",
                    [*prefix, "run", "--rm", "--no-deps", service],
                )
                migration_passes.append(
                    MigrationPassRecord(
                        context=context,
                        service=service,
                        pass_number=pass_number,
                        command=record,
                    )
                )
    except (MigrationProofError, OSError) as exception:
        failure = str(exception)
    finally:
        if not arguments.keep_project:
            try:
                cleanup_record, _ = runner.run(
                    "cleanup-isolated-compose-project",
                    [
                        *prefix,
                        "down",
                        "--volumes",
                        "--remove-orphans",
                        "--timeout",
                        "30",
                    ],
                    check=False,
                )
                if cleanup_record.exit_code != 0 and failure is None:
                    failure = (
                        "Migration proof completed but isolated Compose cleanup failed. "
                        f"Inspect '{cleanup_record.log_path}'."
                    )
            except OSError as exception:
                if failure is None:
                    failure = f"Could not execute isolated Compose cleanup: {exception}"

    finished_at = datetime.now(UTC)
    expected_pass_count = len(contexts) * 2
    if failure is None and len(migration_passes) != expected_pass_count:
        failure = (
            f"Migration proof produced {len(migration_passes)} passes; "
            f"expected {expected_pass_count}."
        )
    report = MigrationProofReport(
        schema_identity="aggregator-backend/migration-proof@1",
        status="passed" if failure is None else "failed",
        repository_root=str(repository_root),
        compose_file=str(compose_file.relative_to(repository_root)),
        environment_file=str(environment_file.relative_to(repository_root)),
        compose_project_name=project_name,
        started_at_utc=started_at.isoformat(),
        finished_at_utc=finished_at.isoformat(),
        contexts=contexts,
        dependency_services=dependency_services,
        configuration_command=configuration_record,
        dependency_start_command=dependency_start_record,
        migration_passes=tuple(migration_passes),
        cleanup_command=cleanup_record,
        failure=failure,
    )
    write_report(report_path, report)
    if failure is not None:
        raise MigrationProofError(
            f"Migration proof failed: {failure} Report: {report_path}"
        )
    return report_path


def main(argv: Sequence[str] | None = None) -> int:
    arguments = parse_arguments(sys.argv[1:] if argv is None else argv)
    try:
        if arguments.self_test:
            run_self_test()
            return 0
        report_path = execute_proof(arguments)
    except MigrationProofError as exception:
        print(str(exception), file=sys.stderr)
        return 1
    print(f"Migration proof passed. Report: {report_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
