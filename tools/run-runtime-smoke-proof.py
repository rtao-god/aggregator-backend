#!/usr/bin/env python3
"""Prove the complete backend runtime topology in an isolated Compose project."""

from __future__ import annotations

import argparse
import json
import sys
import time
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

from repository_proof_runtime import (
    ProofCommandRecord,
    ProofCommandRunner,
    RepositoryProofError,
    compose_prefix,
    find_repository_root,
    make_compose_project_name,
    read_source_identity,
    require_bounded_integer,
    require_repository_path,
    restrict_file_permissions,
    write_json_report,
)

MIGRATION_SERVICES: tuple[str, ...] = (
    "catalog-migrate",
    "query-migrate",
    "ingestion-migrate",
    "analytics-migrate",
    "promotion-migrate",
)
RUNTIME_SERVICES: tuple[str, ...] = (
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
HEALTHCHECK_REQUIRED_SERVICES: tuple[str, ...] = (
    "catalog-api",
    "query-api",
    "ingestion-api",
    "analytics-api",
    "promotion-api",
)
MINIMUM_COMMAND_TIMEOUT_SECONDS = 30
MAXIMUM_COMMAND_TIMEOUT_SECONDS = 7_200
MINIMUM_STARTUP_TIMEOUT_SECONDS = 30
MAXIMUM_STARTUP_TIMEOUT_SECONDS = 1_800
MINIMUM_STABILITY_SECONDS = 1
MAXIMUM_STABILITY_SECONDS = 120


@dataclass(frozen=True)
class RuntimeContainerEvidence:
    service: str
    container_id: str
    image: str
    state: str
    health: str | None
    exit_code: int | None


@dataclass(frozen=True)
class RuntimeSmokeProofReport:
    schema_identity: str
    status: str
    source_commit: str
    source_tree_clean: bool
    allow_dirty: bool
    repository_root: str
    compose_file: str
    environment_file: str
    compose_project_name: str
    command_timeout_seconds: int
    startup_timeout_seconds: int
    stability_seconds: int
    started_at_utc: str
    finished_at_utc: str
    dependency_services: tuple[str, ...]
    configuration_command: ProofCommandRecord | None
    dependency_start_command: ProofCommandRecord | None
    migration_commands: tuple[ProofCommandRecord, ...]
    runtime_start_command: ProofCommandRecord | None
    runtime_state_command: ProofCommandRecord | None
    runtime_evidence: tuple[RuntimeContainerEvidence, ...]
    diagnostic_logs_command: ProofCommandRecord | None
    cleanup_command: ProofCommandRecord | None
    failure: str | None


def parse_arguments(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run migrations and prove every backend runtime deployable in isolated Compose."
    )
    parser.add_argument("--repository-root", default=None)
    parser.add_argument("--compose-file", default="compose.yaml")
    parser.add_argument("--env-file", default=".env")
    parser.add_argument("--results-directory", default="artifacts/runtime-smoke-proof")
    parser.add_argument("--command-timeout-seconds", type=int, default=900)
    parser.add_argument("--startup-timeout-seconds", type=int, default=300)
    parser.add_argument("--stability-seconds", type=int, default=10)
    parser.add_argument("--keep-project", action="store_true")
    parser.add_argument("--allow-dirty", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args(argv)


def parse_compose_configuration(output: str) -> Mapping[str, Any]:
    try:
        parsed = json.loads(output)
    except json.JSONDecodeError as exception:
        raise RepositoryProofError(
            "Docker Compose did not return valid JSON configuration."
        ) from exception
    if not isinstance(parsed, dict) or not isinstance(parsed.get("services"), dict):
        raise RepositoryProofError("Docker Compose configuration has no services object.")
    return parsed


def service_dependencies(service: Mapping[str, Any]) -> tuple[str, ...]:
    depends_on = service.get("depends_on", {})
    if isinstance(depends_on, dict):
        return tuple(str(name) for name in depends_on)
    if isinstance(depends_on, list):
        return tuple(str(name) for name in depends_on)
    raise RepositoryProofError("Compose service depends_on must be an object or an array.")


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
            raise RepositoryProofError(
                f"Compose service '{service_name}' required by runtime proof is missing."
            )
        visited.add(service_name)
        pending.extend(service_dependencies(service))
    return tuple(sorted(visited))


def validate_compose_contract(configuration: Mapping[str, Any]) -> None:
    services = configuration["services"]
    assert isinstance(services, dict)
    required = (*MIGRATION_SERVICES, *RUNTIME_SERVICES)
    missing = [service for service in required if service not in services]
    if missing:
        raise RepositoryProofError(
            "Compose is missing required deployable(s): " + ", ".join(missing)
        )

    for service_name in HEALTHCHECK_REQUIRED_SERVICES:
        service = services[service_name]
        assert isinstance(service, dict)
        healthcheck = service.get("healthcheck")
        if not isinstance(healthcheck, dict) or healthcheck.get("disable") is True:
            raise RepositoryProofError(
                f"Runtime API service '{service_name}' has no active Compose healthcheck."
            )

    for service_name in required:
        service = services[service_name]
        assert isinstance(service, dict)
        image = service.get("image")
        if isinstance(image, str) and _uses_latest_tag(image):
            raise RepositoryProofError(
                f"Runtime service '{service_name}' uses forbidden mutable image '{image}'."
            )


def _uses_latest_tag(image: str) -> bool:
    normalized = image.strip().lower()
    if "@sha256:" in normalized:
        return False
    final_segment = normalized.rsplit("/", 1)[-1]
    return ":" not in final_segment or final_segment.endswith(":latest")


def parse_compose_ps(output: str) -> tuple[Mapping[str, Any], ...]:
    stripped = output.strip()
    if stripped == "":
        return ()
    try:
        parsed = json.loads(stripped)
    except json.JSONDecodeError:
        rows: list[Mapping[str, Any]] = []
        for line in stripped.splitlines():
            try:
                item = json.loads(line)
            except json.JSONDecodeError as exception:
                raise RepositoryProofError(
                    "Docker Compose ps returned invalid JSON evidence."
                ) from exception
            if not isinstance(item, dict):
                raise RepositoryProofError(
                    "Docker Compose ps JSON lines must contain objects."
                )
            rows.append(item)
        return tuple(rows)

    if isinstance(parsed, dict):
        return (parsed,)
    if isinstance(parsed, list) and all(isinstance(item, dict) for item in parsed):
        return tuple(parsed)
    raise RepositoryProofError("Docker Compose ps JSON has an unsupported shape.")


def build_runtime_evidence(
    rows: Iterable[Mapping[str, Any]],
) -> tuple[RuntimeContainerEvidence, ...]:
    by_service: dict[str, list[Mapping[str, Any]]] = {}
    for row in rows:
        service = _read_text(row, "Service", "service")
        if service is None:
            continue
        by_service.setdefault(service, []).append(row)

    evidence: list[RuntimeContainerEvidence] = []
    for service in RUNTIME_SERVICES:
        matches = by_service.get(service, [])
        if len(matches) != 1:
            raise RepositoryProofError(
                f"Runtime service '{service}' has {len(matches)} containers; exactly one is required."
            )
        row = matches[0]
        state = (_read_text(row, "State", "state") or "").lower()
        health = _read_text(row, "Health", "health")
        normalized_health = health.lower() if health else None
        if state != "running":
            raise RepositoryProofError(
                f"Runtime service '{service}' is '{state or 'unknown'}', not running."
            )
        if service in HEALTHCHECK_REQUIRED_SERVICES and normalized_health != "healthy":
            raise RepositoryProofError(
                f"Runtime API service '{service}' is not healthy: '{health or 'missing'}'."
            )
        evidence.append(
            RuntimeContainerEvidence(
                service=service,
                container_id=_read_text(row, "ID", "Id", "id") or "",
                image=_read_text(row, "Image", "image") or "",
                state=state,
                health=normalized_health,
                exit_code=_read_integer(row, "ExitCode", "exit_code"),
            )
        )
    return tuple(evidence)


def _read_text(row: Mapping[str, Any], *keys: str) -> str | None:
    for key in keys:
        value = row.get(key)
        if value is not None:
            return str(value)
    return None


def _read_integer(row: Mapping[str, Any], *keys: str) -> int | None:
    for key in keys:
        value = row.get(key)
        if value is None or value == "":
            continue
        try:
            return int(value)
        except (TypeError, ValueError) as exception:
            raise RepositoryProofError(
                f"Compose ps field '{key}' is not an integer: '{value}'."
            ) from exception
    return None


def run_self_test() -> None:
    configuration = {
        "services": {
            "postgres": {},
            **{
                service: {"depends_on": {"postgres": {}}, "image": "example/app:1.0.0"}
                for service in (*MIGRATION_SERVICES, *RUNTIME_SERVICES)
            },
        }
    }
    for service in HEALTHCHECK_REQUIRED_SERVICES:
        configuration["services"][service]["healthcheck"] = {"test": ["CMD", "true"]}
    validate_compose_contract(configuration)
    closure = dependency_closure(configuration, RUNTIME_SERVICES)
    if "postgres" not in closure:
        raise RepositoryProofError("Runtime dependency closure self-test failed.")
    rows = [
        {
            "Service": service,
            "ID": f"container-{index}",
            "Image": "example/app:1.0.0",
            "State": "running",
            "Health": "healthy" if service in HEALTHCHECK_REQUIRED_SERVICES else "",
            "ExitCode": 0,
        }
        for index, service in enumerate(RUNTIME_SERVICES, start=1)
    ]
    evidence = build_runtime_evidence(rows)
    if len(evidence) != len(RUNTIME_SERVICES):
        raise RepositoryProofError("Runtime evidence self-test failed.")
    if not _uses_latest_tag("example/app") or not _uses_latest_tag("example/app:latest"):
        raise RepositoryProofError("Mutable image detection self-test failed.")
    if _uses_latest_tag("example/app:1.2.3") or _uses_latest_tag("example/app@sha256:" + "a" * 64):
        raise RepositoryProofError("Pinned image detection self-test failed.")
    if len(parse_compose_ps("\n".join(json.dumps(row) for row in rows))) != len(rows):
        raise RepositoryProofError("Compose ps JSON-lines self-test failed.")
    print("Runtime smoke proof self-test passed.")


def execute_proof(arguments: argparse.Namespace) -> Path:
    repository_root = find_repository_root(
        arguments.repository_root,
        "AggregatorBackend.slnx",
    )
    source_identity = read_source_identity(
        repository_root,
        allow_dirty=arguments.allow_dirty,
    )
    compose_file = require_repository_path(
        repository_root,
        arguments.compose_file,
        "Compose file",
    )
    environment_file = require_repository_path(
        repository_root,
        arguments.env_file,
        "Environment file",
    )
    results_parent = require_repository_path(
        repository_root,
        arguments.results_directory,
        "Results directory",
    )
    if not compose_file.is_file():
        raise RepositoryProofError(f"Compose file '{compose_file}' does not exist.")
    if not environment_file.is_file():
        raise RepositoryProofError(
            f"Environment file '{environment_file}' does not exist. "
            "Create it explicitly from .env.example and provide required secrets."
        )

    command_timeout_seconds = require_bounded_integer(
        arguments.command_timeout_seconds,
        MINIMUM_COMMAND_TIMEOUT_SECONDS,
        MAXIMUM_COMMAND_TIMEOUT_SECONDS,
        "Command timeout in seconds",
    )
    startup_timeout_seconds = require_bounded_integer(
        arguments.startup_timeout_seconds,
        MINIMUM_STARTUP_TIMEOUT_SECONDS,
        MAXIMUM_STARTUP_TIMEOUT_SECONDS,
        "Startup timeout in seconds",
    )
    stability_seconds = require_bounded_integer(
        arguments.stability_seconds,
        MINIMUM_STABILITY_SECONDS,
        MAXIMUM_STABILITY_SECONDS,
        "Stability duration in seconds",
    )
    project_name = make_compose_project_name("aggregator-runtime-smoke")
    timestamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%S%fZ")
    results_root = results_parent / timestamp
    results_root.mkdir(parents=True, exist_ok=False)
    restrict_file_permissions(results_root)
    report_path = results_root / "runtime-smoke-proof.json"
    runner = ProofCommandRunner(
        repository_root,
        results_root,
        command_timeout_seconds,
    )
    prefix = compose_prefix(compose_file, environment_file, project_name)
    started_at = datetime.now(UTC)

    configuration_record: ProofCommandRecord | None = None
    dependency_start_record: ProofCommandRecord | None = None
    migration_commands: list[ProofCommandRecord] = []
    runtime_start_record: ProofCommandRecord | None = None
    runtime_state_record: ProofCommandRecord | None = None
    diagnostic_logs_record: ProofCommandRecord | None = None
    cleanup_record: ProofCommandRecord | None = None
    runtime_evidence: tuple[RuntimeContainerEvidence, ...] = ()
    dependency_services: tuple[str, ...] = ()
    failure: str | None = None

    try:
        configuration_record, configuration_output = runner.run(
            "compose-configuration",
            [*prefix, "config", "--format", "json", "--no-interpolate"],
        )
        configuration = parse_compose_configuration(configuration_output)
        validate_compose_contract(configuration)
        roots = (*MIGRATION_SERVICES, *RUNTIME_SERVICES)
        closure = dependency_closure(configuration, roots)
        dependency_services = tuple(service for service in closure if service not in roots)
        if dependency_services:
            dependency_start_record, _ = runner.run(
                "start-runtime-dependencies",
                [
                    *prefix,
                    "up",
                    "--detach",
                    "--wait",
                    "--wait-timeout",
                    str(startup_timeout_seconds),
                    "--remove-orphans",
                    *dependency_services,
                ],
            )

        for service in MIGRATION_SERVICES:
            record, _ = runner.run(
                f"run-{service}",
                [*prefix, "run", "--rm", "--no-deps", service],
            )
            migration_commands.append(record)

        runtime_start_record, _ = runner.run(
            "start-runtime-services",
            [
                *prefix,
                "up",
                "--detach",
                "--wait",
                "--wait-timeout",
                str(startup_timeout_seconds),
                "--remove-orphans",
                *RUNTIME_SERVICES,
            ],
        )
        time.sleep(stability_seconds)
        runtime_state_record, state_output = runner.run(
            "capture-runtime-state",
            [*prefix, "ps", "--all", "--format", "json"],
        )
        runtime_evidence = build_runtime_evidence(parse_compose_ps(state_output))
    except (RepositoryProofError, OSError) as exception:
        failure = str(exception)
        try:
            diagnostic_logs_record, _ = runner.run(
                "capture-runtime-diagnostics",
                [
                    *prefix,
                    "logs",
                    "--no-color",
                    "--timestamps",
                    "--tail",
                    "500",
                    *RUNTIME_SERVICES,
                ],
                check=False,
            )
        except OSError:
            diagnostic_logs_record = None
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
                        "Runtime smoke passed but isolated Compose cleanup failed. "
                        f"Inspect '{cleanup_record.log_path}'."
                    )
            except OSError as exception:
                if failure is None:
                    failure = f"Could not execute isolated Compose cleanup: {exception}"

    if failure is None and len(migration_commands) != len(MIGRATION_SERVICES):
        failure = "Not every migration deployable completed before runtime startup."
    if failure is None and len(runtime_evidence) != len(RUNTIME_SERVICES):
        failure = "Runtime evidence does not cover every canonical deployable."

    report = RuntimeSmokeProofReport(
        schema_identity="aggregator-backend/runtime-smoke-proof@1",
        status="passed" if failure is None else "failed",
        source_commit=source_identity.commit_sha,
        source_tree_clean=source_identity.tree_clean,
        allow_dirty=arguments.allow_dirty,
        repository_root=str(repository_root),
        compose_file=str(compose_file.relative_to(repository_root)),
        environment_file=str(environment_file.relative_to(repository_root)),
        compose_project_name=project_name,
        command_timeout_seconds=command_timeout_seconds,
        startup_timeout_seconds=startup_timeout_seconds,
        stability_seconds=stability_seconds,
        started_at_utc=started_at.isoformat(),
        finished_at_utc=datetime.now(UTC).isoformat(),
        dependency_services=dependency_services,
        configuration_command=configuration_record,
        dependency_start_command=dependency_start_record,
        migration_commands=tuple(migration_commands),
        runtime_start_command=runtime_start_record,
        runtime_state_command=runtime_state_record,
        runtime_evidence=runtime_evidence,
        diagnostic_logs_command=diagnostic_logs_record,
        cleanup_command=cleanup_record,
        failure=failure,
    )
    write_json_report(report_path, report)
    if failure is not None:
        raise RepositoryProofError(
            f"Runtime smoke proof failed: {failure} Report: {report_path}"
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
    print(f"Runtime smoke proof passed. Report: {report_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
