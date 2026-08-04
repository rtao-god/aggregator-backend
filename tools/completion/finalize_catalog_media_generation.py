#!/usr/bin/env python3
"""Apply owner-level contracts after Catalog media source generation."""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APPLICATION_ROOT = ROOT / "src" / "Catalog" / "Catalog.Media.Application"
APPLICATION_PROJECT = APPLICATION_ROOT / "Catalog.Media.Application.csproj"
APPLICATION_SOURCE = APPLICATION_ROOT / "CatalogMediaApplication.cs"
INTERFACE_BLOCK = re.compile(
    r"(?P<header>public interface [^{]+\n\{\n)(?P<body>.*?)(?P<footer>\n\})",
    re.DOTALL,
)


def replace_required(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        if new in text:
            return text
        raise RuntimeError(f"Catalog media generation anchor is missing: {label}")
    return text.replace(old, new, 1)


def add_interface_accessibility(source: str) -> str:
    def normalize(match: re.Match[str]) -> str:
        lines: list[str] = []
        for line in match.group("body").splitlines():
            stripped = line.strip()
            if (
                line.startswith("    ")
                and not line.startswith("        ")
                and stripped
                and not stripped.startswith(("public ", "///", "["))
            ):
                line = f"    public {stripped}"
            lines.append(line)
        return match.group("header") + "\n".join(lines) + match.group("footer")

    return INTERFACE_BLOCK.sub(normalize, source)


def normalize_application_project() -> None:
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


def normalize_application_source() -> None:
    if not APPLICATION_SOURCE.exists():
        raise RuntimeError("Generated Catalog media application source is missing.")

    source = APPLICATION_SOURCE.read_text(encoding="utf-8")
    source = replace_required(
        source,
        """public sealed record CatalogMediaProcessingLease(
    Guid AssetId,
    Guid LeaseToken,
    int AttemptCount,
    DateTimeOffset LeaseExpiresAtUtc,
    CatalogMediaAsset Asset);""",
        """public sealed record CatalogMediaProcessingLease(
    Guid AssetId,
    Guid LeaseToken,
    int AttemptCount,
    DateTimeOffset LeaseExpiresAtUtc,
    long StoredAggregateRevision,
    CatalogMediaAsset Asset);""",
        "processing lease aggregate revision",
    )
    source = add_interface_accessibility(source)
    source = source.replace("        string error,\n", "        string failure,\n")
    source = replace_required(
        source,
        """    {
        var bytes = Serialize(payload);
        return new CatalogMediaOutboxMessage(
""",
        """    {
        ArgumentNullException.ThrowIfNull(context);
        var bytes = Serialize(payload);
        return new CatalogMediaOutboxMessage(
""",
        "outbox command context guard",
    )
    APPLICATION_SOURCE.write_text(source, encoding="utf-8", newline="\n")


def main() -> int:
    normalize_application_project()
    normalize_application_source()
    print("Catalog media generated application contract finalized.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
