"""Shared Docker Compose evidence parsing for repository proof commands."""

from __future__ import annotations

import json
from typing import Any, Iterable, Mapping

from repository_proof_runtime import RepositoryProofError


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
    *,
    proof_name: str,
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
                f"Compose service '{service_name}' required by {proof_name} is missing."
            )
        visited.add(service_name)
        pending.extend(service_dependencies(service))
    return tuple(sorted(visited))


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


def read_text(row: Mapping[str, Any], *keys: str) -> str | None:
    for key in keys:
        value = row.get(key)
        if value is not None:
            return str(value)
    return None


def read_integer(row: Mapping[str, Any], *keys: str) -> int | None:
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


def uses_mutable_image_reference(image: str) -> bool:
    normalized = image.strip().lower()
    if normalized == "":
        return True
    if "@sha256:" in normalized:
        return False
    final_segment = normalized.rsplit("/", 1)[-1]
    return ":" not in final_segment or final_segment.endswith(":latest")
