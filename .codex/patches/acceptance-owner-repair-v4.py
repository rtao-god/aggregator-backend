from __future__ import annotations

from pathlib import Path
import runpy

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
V3_SCRIPT = REPOSITORY_ROOT / ".codex/patches/acceptance-owner-repair-v3.py"


def replace_once(path: Path, old: str, new: str, owner: str) -> None:
    source = path.read_text(encoding="utf-8")
    if old not in source:
        raise SystemExit(f"{owner} anchor is missing in {path.as_posix()}")

    path.write_text(source.replace(old, new, 1), encoding="utf-8", newline="\n")


def main() -> None:
    runpy.run_path(V3_SCRIPT, run_name="__main__")

    replace_once(
        REPOSITORY_ROOT
        / "tests/Acceptance/Acceptance.Control/AcceptanceAnalyticsScenarioService.cs",
        "sourceRevision: 1,",
        "sourceAggregateRevision: 1,",
        "Analytics access projection factory",
    )
    replace_once(
        REPOSITORY_ROOT / "tests/Acceptance/Acceptance.Control/Program.cs",
        "return catalogReady && analyticsReady",
        "return catalogReady.Ready && analyticsReady",
        "Acceptance readiness result",
    )
    replace_once(
        REPOSITORY_ROOT / "tests/Acceptance/Acceptance.Runner/AcceptanceScenario.cs",
        "IReadOnlySet<Guid>? disallowedRevisionIds = null)",
        "HashSet<Guid>? disallowedRevisionIds = null)",
        "Acceptance public-read exclusion set",
    )

    Path(__file__).unlink()


if __name__ == "__main__":
    main()
