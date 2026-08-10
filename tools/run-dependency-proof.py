#!/usr/bin/env python3
"""Audit the exact .NET dependency graph and emit a deterministic CycloneDX SBOM."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import uuid
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence
from urllib.parse import quote

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

SOLUTION = "AggregatorBackend.slnx"
GLOBAL_JSON = "global.json"
CENTRAL_PACKAGES = "Directory.Packages.props"
MINIMUM_COMMAND_TIMEOUT_SECONDS = 60
MAXIMUM_COMMAND_TIMEOUT_SECONDS = 7_200
PACKAGE_ID_PATTERN = re.compile(r"^[^\x00-\x1f\x7f]+$")


@dataclass(frozen=True)
class DependencyComponent:
    package_id: str
    version: str
    dependency_kind: str
    projects: tuple[str, ...]


@dataclass(frozen=True)
class DependencyProofReport:
    schema_identity: str
    status: str
    release_valid: bool
    source_commit: str
    source_tree_clean: bool
    allow_dirty: bool
    repository_root: str
    solution: str
    solution_sha256: str
    global_json: str
    global_json_sha256: str
    central_packages: str
    central_packages_sha256: str
    command_timeout_seconds: int
    started_at_utc: str
    finished_at_utc: str
    restore_audit_command: ProofCommandRecord | None
    inventory_command: ProofCommandRecord | None
    vulnerability_command: ProofCommandRecord | None
    dependency_inventory_path: str | None
    dependency_inventory_sha256: str | None
    sbom_path: str | None
    sbom_sha256: str | None
    component_count: int
    direct_component_count: int
    transitive_component_count: int
    vulnerability_count: int
    failure: str | None


def parse_arguments(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run NuGet audit, inventory all packages and generate CycloneDX evidence."
    )
    parser.add_argument("--repository-root", default=None)
    parser.add_argument("--results-directory", default="artifacts/dependency-proof")
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


def parse_json_object(output: str, description: str) -> Mapping[str, Any]:
    try:
        value = json.loads(output)
    except json.JSONDecodeError as exception:
        raise RepositoryProofError(
            f"{description} did not return valid JSON: {exception}"
        ) from exception
    if not isinstance(value, dict):
        raise RepositoryProofError(f"{description} JSON root must be an object.")
    return value


def require_package_text(value: Any, field: str) -> str:
    if not isinstance(value, str) or value == "" or value.strip() != value:
        raise RepositoryProofError(f"Dependency field '{field}' must be non-empty text.")
    if not PACKAGE_ID_PATTERN.fullmatch(value):
        raise RepositoryProofError(f"Dependency field '{field}' contains control characters.")
    return value


def first_text(value: Mapping[str, Any], *names: str) -> str | None:
    for name in names:
        candidate = value.get(name)
        if isinstance(candidate, str) and candidate != "":
            return candidate
    return None


def collect_components(inventory: Mapping[str, Any]) -> tuple[DependencyComponent, ...]:
    collected: dict[tuple[str, str], dict[str, Any]] = {}
    projects = inventory.get("projects")
    if not isinstance(projects, list):
        raise RepositoryProofError("Package inventory JSON has no projects array.")

    for project_index, project in enumerate(projects):
        if not isinstance(project, dict):
            raise RepositoryProofError(f"Package inventory project {project_index} is invalid.")
        project_path = require_package_text(
            first_text(project, "path", "projectPath", "name") or f"project-{project_index}",
            f"projects[{project_index}]",
        )
        frameworks = project.get("frameworks")
        if not isinstance(frameworks, list):
            raise RepositoryProofError(
                f"Package inventory project '{project_path}' has no frameworks array."
            )
        for framework_index, framework in enumerate(frameworks):
            if not isinstance(framework, dict):
                raise RepositoryProofError(
                    f"Package inventory framework {framework_index} in '{project_path}' is invalid."
                )
            for property_name, dependency_kind in (
                ("topLevelPackages", "direct"),
                ("transitivePackages", "transitive"),
            ):
                packages = framework.get(property_name, [])
                if packages is None:
                    packages = []
                if not isinstance(packages, list):
                    raise RepositoryProofError(
                        f"Package inventory field '{property_name}' in '{project_path}' is invalid."
                    )
                for package_index, package in enumerate(packages):
                    if not isinstance(package, dict):
                        raise RepositoryProofError(
                            f"Package inventory item {package_index} in '{property_name}' is invalid."
                        )
                    package_id = require_package_text(
                        first_text(package, "id", "name") or "",
                        f"{project_path}.{property_name}[{package_index}].id",
                    )
                    version = require_package_text(
                        first_text(package, "resolvedVersion", "version") or "",
                        f"{project_path}.{property_name}[{package_index}].version",
                    )
                    key = (package_id.lower(), version)
                    state = collected.setdefault(
                        key,
                        {
                            "package_id": package_id,
                            "version": version,
                            "direct": False,
                            "projects": set(),
                        },
                    )
                    state["direct"] = bool(state["direct"] or dependency_kind == "direct")
                    state["projects"].add(project_path)

    components = tuple(
        DependencyComponent(
            package_id=state["package_id"],
            version=state["version"],
            dependency_kind="direct" if state["direct"] else "transitive",
            projects=tuple(sorted(state["projects"])),
        )
        for _, state in sorted(
            collected.items(),
            key=lambda item: (item[0][0], item[0][1]),
        )
    )
    if not components:
        raise RepositoryProofError("Package inventory contains no resolved NuGet components.")
    return components


def count_vulnerabilities(value: Any) -> int:
    if isinstance(value, dict):
        count = 0
        for key, child in value.items():
            if key.lower() == "vulnerabilities":
                if child is None:
                    continue
                if not isinstance(child, list):
                    raise RepositoryProofError(
                        "NuGet vulnerability JSON contains a non-array vulnerabilities field."
                    )
                count += len(child)
            else:
                count += count_vulnerabilities(child)
        return count
    if isinstance(value, list):
        return sum(count_vulnerabilities(item) for item in value)
    return 0


def component_purl(component: DependencyComponent) -> str:
    return (
        "pkg:nuget/"
        + quote(component.package_id, safe="")
        + "@"
        + quote(component.version, safe="")
    )


def build_cyclonedx_sbom(
    components: Sequence[DependencyComponent],
    source_commit: str,
    generated_at_utc: datetime,
) -> Mapping[str, Any]:
    root_ref = f"pkg:github/rtao-god/aggregator-backend@{source_commit}"
    component_documents: list[Mapping[str, Any]] = []
    component_refs: list[str] = []
    for component in components:
        purl = component_purl(component)
        component_refs.append(purl)
        component_documents.append(
            {
                "type": "library",
                "bom-ref": purl,
                "name": component.package_id,
                "version": component.version,
                "purl": purl,
                "properties": [
                    {
                        "name": "aggregator-backend:dependency-kind",
                        "value": component.dependency_kind,
                    },
                    {
                        "name": "aggregator-backend:referencing-projects",
                        "value": "\n".join(component.projects),
                    },
                ],
            }
        )
    serial = uuid.uuid5(
        uuid.NAMESPACE_URL,
        f"https://github.com/rtao-god/aggregator-backend/tree/{source_commit}",
    )
    return {
        "bomFormat": "CycloneDX",
        "specVersion": "1.6",
        "serialNumber": f"urn:uuid:{serial}",
        "version": 1,
        "metadata": {
            "timestamp": generated_at_utc.isoformat(),
            "tools": {
                "components": [
                    {
                        "type": "application",
                        "name": "aggregator-backend dependency proof",
                        "version": "1",
                    }
                ]
            },
            "component": {
                "type": "application",
                "bom-ref": root_ref,
                "name": "aggregator-backend",
                "version": source_commit,
                "purl": root_ref,
            },
            "properties": [
                {
                    "name": "aggregator-backend:inventory-model",
                    "value": "flattened resolved NuGet graph",
                }
            ],
        },
        "components": component_documents,
        "dependencies": [
            {
                "ref": root_ref,
                "dependsOn": component_refs,
            },
            *(
                {"ref": component_ref, "dependsOn": []}
                for component_ref in component_refs
            ),
        ],
    }


def validate_sbom(sbom: Mapping[str, Any], expected_components: int) -> None:
    if sbom.get("bomFormat") != "CycloneDX" or sbom.get("specVersion") != "1.6":
        raise RepositoryProofError("Generated SBOM is not CycloneDX 1.6.")
    components = sbom.get("components")
    if not isinstance(components, list) or len(components) != expected_components:
        raise RepositoryProofError("Generated SBOM component count is inconsistent.")
    references: set[str] = set()
    for index, component in enumerate(components):
        if not isinstance(component, dict):
            raise RepositoryProofError(f"SBOM component {index} is invalid.")
        reference = require_package_text(component.get("bom-ref"), f"components[{index}].bom-ref")
        if reference in references:
            raise RepositoryProofError(f"SBOM component reference '{reference}' is duplicated.")
        references.add(reference)
        if component.get("purl") != reference:
            raise RepositoryProofError(f"SBOM component {index} purl does not match bom-ref.")


def write_json_artifact(path: Path, value: Mapping[str, Any]) -> str:
    path.write_text(
        json.dumps(value, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    restrict_file_permissions(path)
    return file_sha256(path)


def run_self_test() -> None:
    inventory = {
        "projects": [
            {
                "path": "src/Test/Test.csproj",
                "frameworks": [
                    {
                        "topLevelPackages": [
                            {"id": "Example.Direct", "resolvedVersion": "1.2.3"}
                        ],
                        "transitivePackages": [
                            {"id": "Example.Transitive", "resolvedVersion": "4.5.6"},
                            {"id": "Example.Direct", "resolvedVersion": "1.2.3"},
                        ],
                    }
                ],
            }
        ]
    }
    components = collect_components(inventory)
    if len(components) != 2 or components[0].dependency_kind != "direct":
        raise RepositoryProofError("Dependency inventory self-test failed.")
    sbom = build_cyclonedx_sbom(
        components,
        "a" * 40,
        datetime(2026, 1, 1, tzinfo=UTC),
    )
    validate_sbom(sbom, 2)
    if count_vulnerabilities({"vulnerabilities": [{"severity": "high"}]}) != 1:
        raise RepositoryProofError("Vulnerability-count self-test failed.")
    if count_vulnerabilities({"projects": []}) != 0:
        raise RepositoryProofError("Empty vulnerability self-test failed.")
    print("Dependency proof self-test passed.")


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
    solution = require_repository_path(repository_root, SOLUTION, "Solution")
    global_json = require_repository_path(repository_root, GLOBAL_JSON, "SDK contract")
    central_packages = require_repository_path(
        repository_root,
        CENTRAL_PACKAGES,
        "Central package contract",
    )
    require_nonempty_file(solution, "Solution")
    require_nonempty_file(global_json, "SDK contract")
    require_nonempty_file(central_packages, "Central package contract")
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
    report_path = results_root / "dependency-proof.json"
    inventory_path = results_root / "dependency-inventory.json"
    sbom_path = results_root / "aggregator-backend.cdx.json"
    runner = ProofCommandRunner(
        repository_root,
        results_root,
        command_timeout_seconds,
    )
    started_at = datetime.now(UTC)
    restore_audit_command: ProofCommandRecord | None = None
    inventory_command: ProofCommandRecord | None = None
    vulnerability_command: ProofCommandRecord | None = None
    inventory_sha256: str | None = None
    sbom_sha256: str | None = None
    components: tuple[DependencyComponent, ...] = ()
    vulnerability_count = 0
    failure: str | None = None

    try:
        restore_audit_command, _ = runner.run(
            "restore-with-nuget-audit",
            [
                "dotnet",
                "restore",
                str(solution),
                "/m:1",
                "/nr:false",
                "--nologo",
                "-p:NuGetAudit=true",
                "-p:NuGetAuditMode=all",
                "-p:WarningsAsErrors=NU1901;NU1902;NU1903;NU1904",
            ],
        )
        inventory_command, inventory_output = runner.run(
            "capture-resolved-package-inventory",
            [
                "dotnet",
                "package",
                "list",
                str(solution),
                "--include-transitive",
                "--format",
                "json",
                "--output-version",
                "1",
            ],
        )
        inventory = parse_json_object(inventory_output, "Resolved package inventory")
        components = collect_components(inventory)
        inventory_sha256 = write_json_artifact(inventory_path, inventory)
        generated_at_utc = datetime.now(UTC)
        sbom = build_cyclonedx_sbom(
            components,
            source_identity.commit_sha,
            generated_at_utc,
        )
        validate_sbom(sbom, len(components))
        sbom_sha256 = write_json_artifact(sbom_path, sbom)

        vulnerability_command, vulnerability_output = runner.run(
            "audit-resolved-package-vulnerabilities",
            [
                "dotnet",
                "package",
                "list",
                str(solution),
                "--vulnerable",
                "--include-transitive",
                "--format",
                "json",
                "--output-version",
                "1",
            ],
        )
        vulnerability_inventory = parse_json_object(
            vulnerability_output,
            "NuGet vulnerability inventory",
        )
        vulnerability_count = count_vulnerabilities(vulnerability_inventory)
        if vulnerability_count != 0:
            raise RepositoryProofError(
                f"NuGet audit reported {vulnerability_count} vulnerable dependency records."
            )
    except (RepositoryProofError, OSError) as exception:
        failure = str(exception)

    direct_component_count = sum(
        1 for component in components if component.dependency_kind == "direct"
    )
    transitive_component_count = len(components) - direct_component_count
    release_valid = failure is None and source_identity.tree_clean and not arguments.allow_dirty
    status = "failed" if failure is not None else ("passed" if release_valid else "diagnostic")
    report = DependencyProofReport(
        schema_identity="aggregator-backend/dependency-proof@1",
        status=status,
        release_valid=release_valid,
        source_commit=source_identity.commit_sha,
        source_tree_clean=source_identity.tree_clean,
        allow_dirty=arguments.allow_dirty,
        repository_root=str(repository_root),
        solution=str(solution.relative_to(repository_root)),
        solution_sha256=file_sha256(solution),
        global_json=str(global_json.relative_to(repository_root)),
        global_json_sha256=file_sha256(global_json),
        central_packages=str(central_packages.relative_to(repository_root)),
        central_packages_sha256=file_sha256(central_packages),
        command_timeout_seconds=command_timeout_seconds,
        started_at_utc=started_at.isoformat(),
        finished_at_utc=datetime.now(UTC).isoformat(),
        restore_audit_command=restore_audit_command,
        inventory_command=inventory_command,
        vulnerability_command=vulnerability_command,
        dependency_inventory_path=(
            str(inventory_path.relative_to(repository_root))
            if inventory_sha256 is not None
            else None
        ),
        dependency_inventory_sha256=inventory_sha256,
        sbom_path=(
            str(sbom_path.relative_to(repository_root))
            if sbom_sha256 is not None
            else None
        ),
        sbom_sha256=sbom_sha256,
        component_count=len(components),
        direct_component_count=direct_component_count,
        transitive_component_count=transitive_component_count,
        vulnerability_count=vulnerability_count,
        failure=failure,
    )
    write_json_report(report_path, report)
    if failure is not None:
        raise RepositoryProofError(
            f"Dependency proof failed: {failure} Report: {report_path}"
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
    print(f"Dependency proof completed. Report: {report_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
