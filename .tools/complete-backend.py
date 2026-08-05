#!/usr/bin/env python3
"""Validate the approved project topology and render canonical .NET solutions.

`docs/architecture/project-topology.json` is the only project-inventory owner.
Physical discovery is validation input only; a new `.csproj` is never included
implicitly.
"""

from __future__ import annotations

from dataclasses import dataclass
import json
import pathlib
import re
import sys
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parents[1]
TOPOLOGY = ROOT / "docs" / "architecture" / "project-topology.json"
SOLUTION = ROOT / "AggregatorBackend.slnx"
RUNTIME_SOLUTION = ROOT / "AggregatorBackend.Runtime.slnx"
INVENTORY = ROOT / "docs" / "decisions" / "project-inventory.md"
PROJECT_REFERENCE_RE = re.compile(r'<ProjectReference\s+Include="([^"]+)"')
ALLOWED_CONTEXTS = {
    "Acceptance", "Analytics", "Architecture", "BuildingBlocks", "Catalog",
    "Ingestion", "Promotion", "Query",
}
ALLOWED_ROLES = {
    "api", "application", "building-block", "contracts", "domain",
    "infrastructure", "migrations", "worker",
}
ALLOWED_TEST_CATEGORIES = {
    "acceptance-support", "api-contract", "architecture",
    "infrastructure-contract", "unit",
}
TEXT_EXTENSIONS = {
    ".cs", ".csproj", ".json", ".md", ".props", ".ps1", ".py", ".sh",
    ".slnx", ".targets", ".yaml", ".yml",
}
EXCLUDED_DIRECTORIES = {".git", ".idea", ".vs", "artifacts", "bin", "obj"}
PRODUCTION_TUPLE = [
    "path", "role", "deployableName", "databaseOwnerOverride",
    "migrationOwnerOverride",
]
TEST_TUPLE = ["path", "testCategory"]


@dataclass(frozen=True)
class ProjectEntry:
    path: str
    bounded_context: str
    role: str
    project_kind: str
    deployable_name: str | None
    database_owner: str | None
    migration_owner: str | None
    test_category: str | None


@dataclass(frozen=True)
class ProjectTopology:
    projects: tuple[ProjectEntry, ...]
    forbidden_project_path_prefixes: tuple[str, ...]
    forbidden_reference_tokens: tuple[str, ...]


def normalize(path: pathlib.Path) -> str:
    return path.relative_to(ROOT).as_posix()


def required_string(value: Any, owner: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise RuntimeError(f"{owner} must be a non-empty string.")
    return value


def optional_string(value: Any, owner: str) -> str | None:
    if value is None:
        return None
    return required_string(value, owner)


def require_relative_path(value: str, owner: str) -> None:
    candidate = pathlib.PurePosixPath(value)
    if (
        "\\" in value
        or candidate.is_absolute()
        or ".." in candidate.parts
        or candidate.as_posix() != value
    ):
        raise RuntimeError(f"{owner} is not a normalized repository-relative path: {value!r}")


def read_string_array(document: dict[str, Any], key: str) -> tuple[str, ...]:
    raw = document.get(key)
    if not isinstance(raw, list) or not raw:
        raise RuntimeError(f"Project topology {key} must be a non-empty array.")
    values = tuple(required_string(value, f"{key} entry") for value in raw)
    if len(values) != len(set(values)):
        raise RuntimeError(f"{key} contains duplicates.")
    return values


def load_topology() -> ProjectTopology:
    if not TOPOLOGY.is_file():
        raise RuntimeError(f"Approved topology manifest is missing: {normalize(TOPOLOGY)}")
    document = json.loads(TOPOLOGY.read_text(encoding="utf-8"))
    if not isinstance(document, dict) or document.get("schemaVersion") != 2:
        raise RuntimeError("Project topology schemaVersion must be exactly 2.")
    if document.get("productionTuple") != PRODUCTION_TUPLE:
        raise RuntimeError("Project topology productionTuple differs from the schema contract.")
    if document.get("testTuple") != TEST_TUPLE:
        raise RuntimeError("Project topology testTuple differs from the schema contract.")

    raw_contexts = document.get("contexts")
    if not isinstance(raw_contexts, list) or not raw_contexts:
        raise RuntimeError("Project topology must contain a non-empty contexts array.")

    contexts_seen: set[str] = set()
    context_names: list[str] = []
    projects: list[ProjectEntry] = []
    for context_index, raw_context in enumerate(raw_contexts):
        owner = f"contexts[{context_index}]"
        if not isinstance(raw_context, dict):
            raise RuntimeError(f"{owner} must be an object.")
        expected_keys = {
            "name", "status", "databaseOwner", "migrationOwner", "production", "tests",
        }
        if set(raw_context) != expected_keys:
            raise RuntimeError(f"{owner} fields differ from the topology contract.")

        context = required_string(raw_context["name"], f"{owner}.name")
        if context not in ALLOWED_CONTEXTS or context in contexts_seen:
            raise RuntimeError(f"Unsupported or duplicate bounded context: {context!r}")
        contexts_seen.add(context)
        context_names.append(context)
        if raw_context["status"] != "active":
            raise RuntimeError(f"{owner}.status must be exactly 'active'.")
        default_database = optional_string(raw_context["databaseOwner"], f"{owner}.databaseOwner")
        default_migration = optional_string(raw_context["migrationOwner"], f"{owner}.migrationOwner")
        if default_migration is not None:
            require_relative_path(default_migration, f"{owner}.migrationOwner")

        production = raw_context["production"]
        tests = raw_context["tests"]
        if not isinstance(production, list) or not isinstance(tests, list):
            raise RuntimeError(f"{owner}.production and {owner}.tests must be arrays.")

        for project_index, raw_project in enumerate(production):
            project_owner = f"{owner}.production[{project_index}]"
            if not isinstance(raw_project, list) or len(raw_project) != len(PRODUCTION_TUPLE):
                raise RuntimeError(f"{project_owner} must match productionTuple.")
            path = required_string(raw_project[0], f"{project_owner}.path")
            role = required_string(raw_project[1], f"{project_owner}.role")
            deployable = optional_string(raw_project[2], f"{project_owner}.deployableName")
            database = optional_string(raw_project[3], f"{project_owner}.databaseOwnerOverride") or default_database
            migration = optional_string(raw_project[4], f"{project_owner}.migrationOwnerOverride") or default_migration
            entry = ProjectEntry(path, context, role, "production", deployable, database, migration, None)
            validate_entry(entry, project_owner)
            projects.append(entry)

        for project_index, raw_project in enumerate(tests):
            project_owner = f"{owner}.tests[{project_index}]"
            if not isinstance(raw_project, list) or len(raw_project) != len(TEST_TUPLE):
                raise RuntimeError(f"{project_owner} must match testTuple.")
            entry = ProjectEntry(
                required_string(raw_project[0], f"{project_owner}.path"),
                context,
                "test",
                "test",
                None,
                None,
                None,
                required_string(raw_project[1], f"{project_owner}.testCategory"),
            )
            validate_entry(entry, project_owner)
            projects.append(entry)

    if context_names != sorted(context_names):
        raise RuntimeError("Project topology contexts must be ordered by name.")
    projects.sort(key=lambda entry: entry.path)
    paths = [entry.path for entry in projects]
    if len(paths) != len(set(paths)):
        raise RuntimeError("Project topology contains duplicate project paths.")
    deployables = [entry.deployable_name for entry in projects if entry.deployable_name]
    if len(deployables) != len(set(deployables)):
        raise RuntimeError("Project topology contains duplicate deployable names.")

    by_path = {entry.path: entry for entry in projects}
    for entry in projects:
        if entry.migration_owner is None:
            continue
        owner = by_path.get(entry.migration_owner)
        if owner is None or owner.role != "migrations" or owner.project_kind != "production":
            raise RuntimeError(f"{entry.path} references invalid migration owner {entry.migration_owner!r}.")
        if owner.database_owner != entry.database_owner:
            raise RuntimeError(f"{entry.path} and its migration owner disagree on database ownership.")

    forbidden_prefixes = read_string_array(document, "forbiddenProjectPathPrefixes")
    for prefix in forbidden_prefixes:
        require_relative_path(prefix, "forbiddenProjectPathPrefixes entry")
    return ProjectTopology(
        tuple(projects),
        forbidden_prefixes,
        read_string_array(document, "forbiddenReferenceTokens"),
    )


def validate_entry(entry: ProjectEntry, owner: str) -> None:
    require_relative_path(entry.path, f"{owner}.path")
    expected_root = "src/" if entry.project_kind == "production" else "tests/"
    if not entry.path.startswith(expected_root) or not entry.path.endswith(".csproj"):
        raise RuntimeError(f"{owner}.path does not match project kind: {entry.path}")
    if entry.project_kind == "production":
        if entry.role not in ALLOWED_ROLES or entry.test_category is not None:
            raise RuntimeError(f"{owner} has unsupported production metadata.")
        if entry.bounded_context == "BuildingBlocks":
            if entry.role != "building-block" or entry.database_owner or entry.migration_owner:
                raise RuntimeError(f"{owner} violates BuildingBlocks ownership.")
        elif entry.database_owner is None or entry.migration_owner is None:
            raise RuntimeError(f"{owner} must declare database and migration ownership.")
    elif entry.role != "test" or entry.test_category not in ALLOWED_TEST_CATEGORIES:
        raise RuntimeError(f"{owner} has unsupported test metadata.")

    if entry.deployable_name is not None:
        if entry.role not in {"api", "migrations", "worker"}:
            raise RuntimeError(f"{owner} declares a deployable for non-deployable role {entry.role}.")
        if not re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", entry.deployable_name):
            raise RuntimeError(f"{owner}.deployableName is not kebab-case.")


def discover_projects() -> list[pathlib.Path]:
    projects: list[pathlib.Path] = []
    for root_name in ("src", "tests"):
        root = ROOT / root_name
        if root.exists():
            projects.extend(root.rglob("*.csproj"))
    return sorted({path.resolve() for path in projects}, key=normalize)


def verify_physical_topology(topology: ProjectTopology) -> list[pathlib.Path]:
    physical = [normalize(path) for path in discover_projects()]
    approved = [entry.path for entry in topology.projects]
    missing = sorted(set(approved) - set(physical))
    unknown = sorted(set(physical) - set(approved))
    if missing or unknown:
        raise RuntimeError(
            "Physical projects differ from approved topology.\n"
            f"Missing:\n{render_items(missing)}\nUnknown:\n{render_items(unknown)}"
        )
    forbidden = [
        path for path in physical
        if any(path.startswith(prefix) for prefix in topology.forbidden_project_path_prefixes)
    ]
    if forbidden:
        raise RuntimeError("Forbidden project contours exist:\n" + render_items(forbidden))
    return [ROOT / entry.path for entry in topology.projects]


def verify_references(projects: list[pathlib.Path]) -> None:
    known = {project.resolve() for project in projects}
    failures: list[str] = []
    for project in projects:
        for raw_reference in PROJECT_REFERENCE_RE.findall(project.read_text(encoding="utf-8")):
            target = (project.parent / raw_reference.replace("\\", "/")).resolve()
            if target not in known or not target.exists():
                failures.append(f"{normalize(project)} -> {raw_reference}")
    if failures:
        raise RuntimeError("Broken or non-approved ProjectReference edges:\n" + render_items(failures))


def verify_forbidden_references(topology: ProjectTopology) -> None:
    failures: list[str] = []
    for path in ROOT.rglob("*"):
        if not path.is_file() or path.resolve() == TOPOLOGY.resolve():
            continue
        if any(part in EXCLUDED_DIRECTORIES for part in path.relative_to(ROOT).parts):
            continue
        if path.suffix.lower() not in TEXT_EXTENSIONS:
            continue
        try:
            content = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue
        for token in topology.forbidden_reference_tokens:
            if token in content:
                failures.append(f"{normalize(path)} contains {token!r}")
    if failures:
        raise RuntimeError("Forbidden obsolete topology references exist:\n" + render_items(sorted(failures)))


def folder_name(project: pathlib.Path) -> str:
    parts = project.relative_to(ROOT).parts
    if parts[0] == "src" and len(parts) >= 3:
        return f"/src/{parts[1]}/"
    if parts[0] == "tests":
        return f"/tests/{parts[1]}/" if len(parts) > 2 else "/tests/"
    raise RuntimeError(f"Project lies outside supported roots: {project}")


def render_solution(projects: list[pathlib.Path]) -> str:
    grouped: dict[str, list[str]] = {}
    for project in projects:
        grouped.setdefault(folder_name(project), []).append(normalize(project))
    lines = ["<Solution>"]
    for folder in sorted(grouped, key=lambda value: (value.count("/"), value)):
        lines.append(f'  <Folder Name="{folder}">')
        lines.extend(f'    <Project Path="{path}" />' for path in sorted(grouped[folder]))
        lines.append("  </Folder>")
    return "\n".join([*lines, "</Solution>", ""])


def render_inventory(topology: ProjectTopology) -> str:
    production = [entry for entry in topology.projects if entry.project_kind == "production"]
    tests = [entry for entry in topology.projects if entry.project_kind == "test"]
    counts: dict[str, int] = {}
    for entry in topology.projects:
        counts[entry.bounded_context] = counts.get(entry.bounded_context, 0) + 1
    rows = ["| Context | Projects |", "| --- | ---: |"]
    rows.extend(f"| {context} | {count} |" for context, count in sorted(counts.items()))
    return "\n".join([
        "# Approved project inventory", "",
        "This file is generated by `.tools/complete-backend.py` from `docs/architecture/project-topology.json`. The JSON manifest is the only project-topology owner; physical discovery is validation input, never an inclusion rule.",
        "", f"- Approved projects: **{len(topology.projects)}**",
        f"- Production projects: **{len(production)}**",
        f"- Test/support projects: **{len(tests)}**", "", *rows, "",
        "## Enforcement", "",
        "- `AggregatorBackend.slnx` contains exactly all approved projects.",
        "- `AggregatorBackend.Runtime.slnx` contains exactly approved production projects.",
        "- Unknown, missing, forbidden, or duplicate projects fail before restore.",
        "- Every `ProjectReference` must resolve to an approved project.",
        "- Obsolete owner contours and their configuration references are forbidden repository-wide.", "",
    ])


def render_items(items: list[str]) -> str:
    return "\n".join(f"- {item}" for item in items) if items else "- (none)"


def write_or_check(path: pathlib.Path, expected: str, check: bool) -> bool:
    actual = path.read_text(encoding="utf-8") if path.exists() else None
    if actual == expected:
        return False
    if check:
        print(f"stale: {normalize(path)}", file=sys.stderr)
        return True
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(expected, encoding="utf-8", newline="\n")
    print(f"updated: {normalize(path)}")
    return True


def main() -> int:
    check = "--check" in sys.argv[1:]
    topology = load_topology()
    projects = verify_physical_topology(topology)
    if not projects:
        raise RuntimeError("No approved projects exist.")
    verify_references(projects)
    verify_forbidden_references(topology)
    runtime = [
        ROOT / entry.path for entry in topology.projects
        if entry.project_kind == "production"
    ]
    stale = False
    stale |= write_or_check(SOLUTION, render_solution(projects), check)
    stale |= write_or_check(RUNTIME_SOLUTION, render_solution(runtime), check)
    stale |= write_or_check(INVENTORY, render_inventory(topology), check)
    return 1 if check and stale else 0


if __name__ == "__main__":
    raise SystemExit(main())
