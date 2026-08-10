#!/usr/bin/env python3
from __future__ import annotations

import json
import pathlib
import sys
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "contracts" / "runtime-contract-manifest.json"
STRICT_JSON_REQUIRED = (
    "UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow",
    "allowIntegerValues: false",
)


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


def normalize_entry(section: str, raw_entry: Any) -> dict[str, Any]:
    if isinstance(raw_entry, dict):
        if "file" not in raw_entry:
            raise AssertionError(f"{section} entry does not declare a file: {raw_entry!r}")
        return raw_entry

    if section == "strictJsonPrograms" and isinstance(raw_entry, str):
        return {
            "name": raw_entry,
            "file": raw_entry,
            "required": list(STRICT_JSON_REQUIRED),
        }

    raise AssertionError(
        f"{section} entry must be an object with file/required fields: {raw_entry!r}"
    )


def main() -> int:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    failures: list[str] = []
    identities: dict[str, str] = {}

    for section in ("contracts", "http", "workers", "strictJsonPrograms"):
        seen_names: set[str] = set()
        for raw_entry in manifest.get(section, []):
            try:
                entry = normalize_entry(section, raw_entry)
                name = str(entry.get("name", entry["file"]))
                if name in seen_names:
                    failures.append(f"{section}: duplicate entry name {name!r}")
                seen_names.add(name)
                failures.extend(verify_required(entry))
            except (AssertionError, KeyError, TypeError, ValueError) as exception:
                failures.append(f"{section}: {exception}")
                continue

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

    if failures:
        print("Runtime contract verification failed:", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
        return 1

    print(
        f"Runtime contract verification succeeded for "
        f"{len(manifest.get('contracts', []))} contracts, "
        f"{len(manifest.get('http', []))} HTTP boundaries, "
        f"{len(manifest.get('workers', []))} worker boundaries, and "
        f"{len(manifest.get('strictJsonPrograms', []))} strict JSON programs."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
