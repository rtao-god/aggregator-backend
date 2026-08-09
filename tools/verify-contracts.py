#!/usr/bin/env python3
from __future__ import annotations

import json
import pathlib
import sys
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "contracts" / "runtime-contract-manifest.json"


def read_text(relative_path: str) -> str:
    path = ROOT / relative_path
    if not path.is_file():
        raise AssertionError(f"Contract file is missing: {relative_path}")
    return path.read_text(encoding="utf-8")


def verify_required(entry: dict[str, Any]) -> list[str]:
    name = str(entry.get("name", entry.get("file", "unknown")))
    relative_path = str(entry["file"])
    content = read_text(relative_path)
    failures: list[str] = []
    for required in entry.get("required", []):
        required_text = str(required)
        occurrences = content.count(required_text)
        if occurrences != 1:
            failures.append(
                f"{name}: expected exactly one occurrence of {required_text!r} "
                f"in {relative_path}, found {occurrences}"
            )
    for forbidden in entry.get("forbidden", []):
        forbidden_text = str(forbidden)
        if forbidden_text in content:
            failures.append(
                f"{name}: forbidden token {forbidden_text!r} exists in {relative_path}"
            )
    return failures


def main() -> int:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    failures: list[str] = []
    identities: dict[str, str] = {}

    for section in ("contracts", "http", "workers"):
        for entry in manifest.get(section, []):
            failures.extend(verify_required(entry))
            if section == "contracts":
                content = read_text(str(entry["file"]))
                for line in content.splitlines():
                    stripped = line.strip()
                    if "public const string" not in stripped or "=" not in stripped:
                        continue
                    value = stripped.split("=", maxsplit=1)[1].strip().rstrip(";").strip('"')
                    if "@" not in value and "." not in value:
                        continue
                    previous = identities.setdefault(value, str(entry["file"]))
                    if previous != str(entry["file"]):
                        failures.append(
                            f"Contract identity {value!r} is declared in both "
                            f"{previous} and {entry['file']}"
                        )

    for relative_path in manifest.get("strictJsonPrograms", []):
        content = read_text(str(relative_path))
        for token in (
            "UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow",
            "allowIntegerValues: false",
        ):
            if token not in content:
                failures.append(
                    f"Strict JSON token {token!r} is missing from {relative_path}"
                )

    if failures:
        print("Runtime contract verification failed:", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
        return 1

    print(
        f"Runtime contract verification succeeded for "
        f"{len(manifest.get('contracts', []))} contracts, "
        f"{len(manifest.get('http', []))} HTTP boundaries, and "
        f"{len(manifest.get('workers', []))} worker boundaries."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
