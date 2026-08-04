#!/usr/bin/env python3
"""Apply owner-level project contracts after Catalog media core layout normalization."""

from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APPLICATION_PROJECT = (
    ROOT
    / "src"
    / "Catalog"
    / "Catalog.Media.Application"
    / "Catalog.Media.Application.csproj"
)


def replace_required(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        if new in text:
            return text
        raise RuntimeError(f"Catalog media generation anchor is missing: {label}")
    return text.replace(old, new, 1)


def main() -> int:
    if not APPLICATION_PROJECT.exists():
        raise RuntimeError(
            "Catalog.Media.Application must be generated and layout-normalized first."
        )

    source = APPLICATION_PROJECT.read_text(encoding="utf-8")
    source = replace_required(
        source,
        '<Project Sdk="Microsoft.NET.Sdk">\n  <ItemGroup>\n',
        '<Project Sdk="Microsoft.NET.Sdk">\n'
        '  <ItemGroup>\n'
        '    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />\n'
        '  </ItemGroup>\n'
        '  <ItemGroup>\n',
        "application dependency-injection abstraction",
    )
    APPLICATION_PROJECT.write_text(source, encoding="utf-8", newline="\n")
    print("Catalog media generated project contracts finalized.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
