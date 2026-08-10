#!/usr/bin/env python3
from __future__ import annotations

import argparse
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import TextIO

TRX_NAMESPACE = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
TRX = {"t": TRX_NAMESPACE}


class DiscoveryGuardError(RuntimeError):
    pass


@dataclass(frozen=True)
class TestCounters:
    total: int
    executed: int
    passed: int
    failed: int
    error: int
    timeout: int
    aborted: int
    inconclusive: int
    not_executed: int

    @property
    def unsuccessful(self) -> int:
        return self.failed + self.error + self.timeout + self.aborted


@dataclass(frozen=True)
class ProjectResult:
    project: Path
    trx_path: Path
    counters: TestCounters


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Run every repository test project independently and fail unless each "
            "project produces a valid TRX document with executed tests."
        )
    )
    parser.add_argument(
        "--solution",
        default="AggregatorBackend.slnx",
        help="Repository-relative .slnx file used as the canonical test-project inventory.",
    )
    parser.add_argument(
        "--results-directory",
        required=False,
        help="Directory for deterministic per-project TRX files.",
    )
    parser.add_argument(
        "--diagnostics-file",
        required=False,
        help="Combined dotnet output and discovery-guard diagnostics.",
    )
    parser.add_argument(
        "--dotnet",
        default="dotnet",
        help="dotnet executable path.",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="Run parser/inventory self-tests without invoking dotnet.",
    )
    return parser.parse_args()


def find_repository_root(start: Path) -> Path:
    current = start.resolve()
    for candidate in (current, *current.parents):
        if (candidate / "AggregatorBackend.slnx").is_file():
            return candidate
    raise DiscoveryGuardError("Repository root containing AggregatorBackend.slnx was not found.")


def discover_test_projects(repository_root: Path, solution_path: Path) -> list[Path]:
    if not solution_path.is_file():
        raise DiscoveryGuardError(f"Solution inventory '{solution_path}' was not found.")

    try:
        root = ET.parse(solution_path).getroot()
    except ET.ParseError as exception:
        raise DiscoveryGuardError(
            f"Solution inventory '{solution_path}' is not valid XML: {exception}."
        ) from exception

    projects: list[Path] = []
    seen: set[Path] = set()
    for node in root.findall(".//Project"):
        raw_path = node.get("Path")
        if raw_path is None:
            raise DiscoveryGuardError("Solution inventory contains a Project without Path.")
        relative = Path(raw_path.replace("\\", "/"))
        if not relative.as_posix().startswith("tests/"):
            continue
        if not relative.stem.endswith(".Tests"):
            continue

        absolute = (repository_root / relative).resolve()
        try:
            absolute.relative_to(repository_root)
        except ValueError as exception:
            raise DiscoveryGuardError(
                f"Test project '{relative}' escapes the repository root."
            ) from exception
        if absolute in seen:
            raise DiscoveryGuardError(
                f"Test project '{relative}' is duplicated in the solution inventory."
            )
        if not absolute.is_file():
            raise DiscoveryGuardError(
                f"Test project '{relative}' declared by the solution does not exist."
            )
        seen.add(absolute)
        projects.append(absolute)

    projects.sort(key=lambda path: path.relative_to(repository_root).as_posix())
    if not projects:
        raise DiscoveryGuardError(
            "The canonical solution contains no tests/**/**/*.Tests.csproj projects."
        )

    duplicate_names = sorted(
        name
        for name in {path.stem for path in projects}
        if sum(path.stem == name for path in projects) > 1
    )
    if duplicate_names:
        raise DiscoveryGuardError(
            "Per-project TRX names would collide: " + ", ".join(duplicate_names)
        )
    return projects


def parse_non_negative_int(counters: ET.Element, attribute: str) -> int:
    raw = counters.get(attribute)
    if raw is None:
        raise DiscoveryGuardError(
            f"TRX Counters element is missing required '{attribute}' attribute."
        )
    try:
        value = int(raw)
    except ValueError as exception:
        raise DiscoveryGuardError(
            f"TRX counter '{attribute}' is not an integer: {raw!r}."
        ) from exception
    if value < 0:
        raise DiscoveryGuardError(
            f"TRX counter '{attribute}' cannot be negative: {value}."
        )
    return value


def parse_trx(trx_path: Path) -> TestCounters:
    if not trx_path.is_file():
        raise DiscoveryGuardError(f"Expected TRX result '{trx_path}' was not produced.")
    try:
        root = ET.parse(trx_path).getroot()
    except ET.ParseError as exception:
        raise DiscoveryGuardError(
            f"TRX result '{trx_path}' is invalid XML: {exception}."
        ) from exception

    summary = root.find("t:ResultSummary", TRX)
    if summary is None:
        raise DiscoveryGuardError(
            f"TRX result '{trx_path}' has no ResultSummary."
        )
    counters_element = summary.find("t:Counters", TRX)
    if counters_element is None:
        raise DiscoveryGuardError(
            f"TRX result '{trx_path}' has no ResultSummary/Counters."
        )

    counters = TestCounters(
        total=parse_non_negative_int(counters_element, "total"),
        executed=parse_non_negative_int(counters_element, "executed"),
        passed=parse_non_negative_int(counters_element, "passed"),
        failed=parse_non_negative_int(counters_element, "failed"),
        error=parse_non_negative_int(counters_element, "error"),
        timeout=parse_non_negative_int(counters_element, "timeout"),
        aborted=parse_non_negative_int(counters_element, "aborted"),
        inconclusive=parse_non_negative_int(counters_element, "inconclusive"),
        not_executed=parse_non_negative_int(counters_element, "notExecuted"),
    )
    if counters.total < 1:
        raise DiscoveryGuardError(
            f"TRX result '{trx_path}' discovered zero tests."
        )
    if counters.executed < 1:
        raise DiscoveryGuardError(
            f"TRX result '{trx_path}' executed zero tests."
        )
    if counters.executed > counters.total:
        raise DiscoveryGuardError(
            f"TRX result '{trx_path}' executed more tests than it discovered."
        )
    if counters.passed + counters.unsuccessful + counters.inconclusive > counters.executed:
        raise DiscoveryGuardError(
            f"TRX result '{trx_path}' contains inconsistent executed counters."
        )
    return counters


def write_line(output: TextIO, diagnostics: TextIO, value: str = "") -> None:
    print(value, file=output, flush=True)
    print(value, file=diagnostics, flush=True)


def run_project(
    repository_root: Path,
    project: Path,
    results_directory: Path,
    dotnet: str,
    diagnostics: TextIO,
) -> ProjectResult:
    relative = project.relative_to(repository_root)
    trx_path = results_directory / f"{project.stem}.trx"
    if trx_path.exists():
        trx_path.unlink()

    command = [
        dotnet,
        "test",
        str(relative),
        "--no-build",
        "--no-restore",
        "-warnaserror",
        "--logger",
        f"trx;LogFileName={trx_path.name}",
        "--results-directory",
        str(results_directory),
        "/m:1",
        "/nr:false",
    ]
    header = f"[test-discovery] project={relative.as_posix()}"
    write_line(sys.stdout, diagnostics, header)
    write_line(sys.stdout, diagnostics, "$ " + " ".join(command))

    process = subprocess.Popen(
        command,
        cwd=repository_root,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    assert process.stdout is not None
    for line in process.stdout:
        sys.stdout.write(line)
        sys.stdout.flush()
        diagnostics.write(line)
        diagnostics.flush()
    exit_code = process.wait()

    if exit_code != 0:
        raise DiscoveryGuardError(
            f"dotnet test failed for '{relative.as_posix()}' with exit code {exit_code}."
        )
    counters = parse_trx(trx_path)
    if counters.unsuccessful > 0:
        raise DiscoveryGuardError(
            f"TRX result for '{relative.as_posix()}' contains "
            f"{counters.unsuccessful} unsuccessful test(s)."
        )
    write_line(
        sys.stdout,
        diagnostics,
        "[test-discovery] "
        f"passed project={relative.as_posix()} total={counters.total} "
        f"executed={counters.executed} passed={counters.passed} "
        f"notExecuted={counters.not_executed}",
    )
    return ProjectResult(project, trx_path, counters)


def run_self_test() -> None:
    with tempfile.TemporaryDirectory(prefix="test-discovery-guard-") as directory:
        root = Path(directory)
        solution = root / "AggregatorBackend.slnx"
        project = root / "tests" / "Sample.Tests" / "Sample.Tests.csproj"
        project.parent.mkdir(parents=True)
        project.write_text("<Project Sdk=\"Microsoft.NET.Sdk\" />\n", encoding="utf-8")
        solution.write_text(
            "<Solution><Folder Name=\"/tests/\">"
            "<Project Path=\"tests/Sample.Tests/Sample.Tests.csproj\" />"
            "</Folder></Solution>\n",
            encoding="utf-8",
        )
        discovered = discover_test_projects(root, solution)
        if discovered != [project.resolve()]:
            raise DiscoveryGuardError("Self-test failed to discover the exact sample project.")

        trx = root / "sample.trx"
        trx.write_text(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            f"<TestRun xmlns=\"{TRX_NAMESPACE}\">"
            "<ResultSummary outcome=\"Completed\">"
            "<Counters total=\"2\" executed=\"2\" passed=\"2\" failed=\"0\" "
            "error=\"0\" timeout=\"0\" aborted=\"0\" inconclusive=\"0\" "
            "notExecuted=\"0\" />"
            "</ResultSummary></TestRun>",
            encoding="utf-8",
        )
        counters = parse_trx(trx)
        if counters.total != 2 or counters.passed != 2:
            raise DiscoveryGuardError("Self-test parsed unexpected TRX counters.")

        empty_trx = root / "empty.trx"
        empty_trx.write_text(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            f"<TestRun xmlns=\"{TRX_NAMESPACE}\">"
            "<ResultSummary outcome=\"Completed\">"
            "<Counters total=\"0\" executed=\"0\" passed=\"0\" failed=\"0\" "
            "error=\"0\" timeout=\"0\" aborted=\"0\" inconclusive=\"0\" "
            "notExecuted=\"0\" />"
            "</ResultSummary></TestRun>",
            encoding="utf-8",
        )
        try:
            parse_trx(empty_trx)
        except DiscoveryGuardError:
            pass
        else:
            raise DiscoveryGuardError("Self-test accepted a zero-test TRX result.")

    print("Test discovery guard self-test passed.")


def main() -> int:
    args = parse_arguments()
    if args.self_test:
        run_self_test()
        return 0

    repository_root = find_repository_root(Path(__file__).parent)
    solution_path = (repository_root / args.solution).resolve()
    projects = discover_test_projects(repository_root, solution_path)
    results_directory = Path(
        args.results_directory
        or repository_root / "artifacts" / "test-results"
    ).resolve()
    diagnostics_path = Path(
        args.diagnostics_file
        or results_directory / "console.log"
    ).resolve()
    results_directory.mkdir(parents=True, exist_ok=True)
    diagnostics_path.parent.mkdir(parents=True, exist_ok=True)

    results: list[ProjectResult] = []
    failure: DiscoveryGuardError | None = None
    with diagnostics_path.open("w", encoding="utf-8", newline="\n") as diagnostics:
        write_line(
            sys.stdout,
            diagnostics,
            f"[test-discovery] projects={len(projects)} solution={solution_path.relative_to(repository_root)}",
        )
        for project in projects:
            try:
                results.append(
                    run_project(
                        repository_root,
                        project,
                        results_directory,
                        args.dotnet,
                        diagnostics,
                    )
                )
            except DiscoveryGuardError as exception:
                failure = exception
                write_line(sys.stderr, diagnostics, f"[test-discovery] failure: {exception}")
                break

        total = sum(result.counters.total for result in results)
        executed = sum(result.counters.executed for result in results)
        passed = sum(result.counters.passed for result in results)
        write_line(
            sys.stdout,
            diagnostics,
            "[test-discovery] summary "
            f"projectsPassed={len(results)}/{len(projects)} "
            f"total={total} executed={executed} passed={passed}",
        )

    if failure is not None:
        return 1
    if len(results) != len(projects):
        print(
            "[test-discovery] failure: not every declared test project was executed.",
            file=sys.stderr,
        )
        return 1
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except DiscoveryGuardError as exception:
        print(f"[test-discovery] failure: {exception}", file=sys.stderr)
        raise SystemExit(1) from exception
