#!/usr/bin/env python3
"""Keep context application/infrastructure composition roots aligned with production types.

This is intentionally conservative: it registers only public concrete owner services and
explicit owner-port adapters that follow repository naming conventions. It never scans tests,
never registers domain aggregates, and never creates a placeholder host.
"""

from __future__ import annotations

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
SRC = ROOT / "src"
CONTEXTS = ("Catalog", "Query", "Ingestion", "Analytics", "Promotion")
CLASS_RE = re.compile(
    r"public\s+(?:sealed\s+)?class\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)"
    r"(?:\s*\([^)]*\))?\s*(?::\s*(?P<bases>[^\{\n]+))?"
)


def read(path: pathlib.Path) -> str:
    return path.read_text(encoding="utf-8")


def concrete_classes(folder: pathlib.Path) -> list[tuple[str, str]]:
    result: list[tuple[str, str]] = []
    if not folder.exists():
        return result
    for path in sorted(folder.rglob("*.cs")):
        if path.name.endswith("ServiceCollectionExtensions.cs") or "/obj/" in path.as_posix():
            continue
        text = read(path)
        for match in CLASS_RE.finditer(text):
            result.append((match.group("name"), (match.group("bases") or "").strip()))
    return result


def find_extension(folder: pathlib.Path, expected_name: str) -> pathlib.Path | None:
    candidates = sorted(folder.rglob("*ServiceCollectionExtensions.cs")) if folder.exists() else []
    for candidate in candidates:
        if expected_name in read(candidate):
            return candidate
    return candidates[0] if len(candidates) == 1 else None


def add_statements(path: pathlib.Path, method_name: str, statements: list[str], check: bool) -> bool:
    if not statements:
        return False
    text = read(path)
    missing = [statement for statement in statements if statement not in text]
    if not missing:
        return False

    method = re.search(
        rf"(?P<head>public\s+static\s+IServiceCollection\s+{re.escape(method_name)}\s*\([^)]*\)\s*\{{)",
        text,
        flags=re.MULTILINE,
    )
    if method is None:
        raise RuntimeError(f"Cannot locate {method_name} in {path.relative_to(ROOT)}")

    return_match = re.search(r"^\s*return\s+services;\s*$", text[method.end():], flags=re.MULTILINE)
    if return_match is None:
        raise RuntimeError(f"Cannot locate owner return statement in {path.relative_to(ROOT)}")
    insertion = method.end() + return_match.start()
    indent = "        "
    block = "".join(f"{indent}{statement}\n" for statement in sorted(missing))
    updated = text[:insertion] + block + text[insertion:]
    if check:
        print(f"stale composition: {path.relative_to(ROOT).as_posix()}", file=sys.stderr)
        return True
    path.write_text(updated, encoding="utf-8", newline="\n")
    print(f"updated composition: {path.relative_to(ROOT).as_posix()}")
    return True


def application_statements(context: str) -> tuple[pathlib.Path | None, list[str]]:
    folder = SRC / context / f"{context}.Application"
    extension = find_extension(folder, f"Add{context}Application")
    statements: list[str] = []
    for name, bases in concrete_classes(folder):
        if not name.endswith("Service"):
            continue
        if "Exception" in name or name.endswith("BackgroundService"):
            continue
        statements.append(f"services.AddScoped<{name}>();")
    return extension, statements


def infrastructure_statements(context: str) -> tuple[pathlib.Path | None, list[str]]:
    folder = SRC / context / f"{context}.Infrastructure"
    extension = find_extension(folder, f"Add{context}Infrastructure")
    statements: list[str] = []
    for name, bases in concrete_classes(folder):
        base_names = [item.strip().split("<", 1)[0] for item in bases.split(",") if item.strip()]
        owner_interfaces = [
            value for value in base_names
            if value.startswith("I")
            and value not in {"IDisposable", "IAsyncDisposable", "IHostedService"}
            and (
                value.startswith(f"I{context}")
                or value.startswith("ICatalog")
                or value.startswith("IQuery")
                or value.startswith("IIngestion")
                or value.startswith("IAnalytics")
                or value.startswith("IPromotion")
            )
        ]
        for interface in owner_interfaces:
            lifetime = "Singleton" if name.startswith(("System", "UuidV7")) else "Scoped"
            statements.append(f"services.Add{lifetime}<{interface}, {name}>();")
        if name.endswith("ReadinessProbe"):
            statements.append(f"services.AddScoped<{name}>();")
    return extension, statements


def worker_statements(context: str) -> tuple[pathlib.Path | None, list[str]]:
    folder = SRC / context / f"{context}.Worker"
    extension = find_extension(folder, f"Add{context}Worker")
    statements: list[str] = []
    for name, bases in concrete_classes(folder):
        if "BackgroundService" in bases or name.endswith("WorkerService"):
            statements.append(f"services.AddHostedService<{name}>();")
    return extension, statements


def reconcile(check: bool) -> bool:
    stale = False
    for context in CONTEXTS:
        for extension, statements, method in (
            (*application_statements(context), f"Add{context}Application"),
            (*infrastructure_statements(context), f"Add{context}Infrastructure"),
            (*worker_statements(context), f"Add{context}Worker"),
        ):
            if extension is None:
                if statements:
                    raise RuntimeError(
                        f"{context} has production types requiring {method}, but no unambiguous composition extension."
                    )
                continue
            stale |= add_statements(extension, method, statements, check)
    return stale


def main() -> int:
    check = "--check" in sys.argv[1:]
    stale = reconcile(check)
    return 1 if check and stale else 0


if __name__ == "__main__":
    raise SystemExit(main())
