"""Technical runtime shared only by repository release-proof commands.

This module owns bounded subprocess execution, exact log evidence, source-tree
identity and isolated Docker Compose project naming. It contains no business or
bounded-context meaning.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import subprocess
import time
from dataclasses import asdict, dataclass, is_dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Mapping, Sequence

PROJECT_NAME_PATTERN = re.compile(r"^[a-z0-9][a-z0-9_-]*$")
GIT_COMMIT_PATTERN = re.compile(r"^[0-9a-f]{40}$")


class RepositoryProofError(RuntimeError):
    """Fail-closed repository proof error."""


@dataclass(frozen=True)
class RepositorySourceIdentity:
    commit_sha: str
    tree_clean: bool


@dataclass(frozen=True)
class ProofCommandRecord:
    purpose: str
    command: tuple[str, ...]
    started_at_utc: str
    finished_at_utc: str
    duration_seconds: float
    exit_code: int
    timed_out: bool
    log_path: str
    log_sha256: str


class ProofCommandRunner:
    def __init__(
        self,
        repository_root: Path,
        results_directory: Path,
        command_timeout_seconds: int,
    ) -> None:
        self._repository_root = repository_root
        self._results_directory = results_directory
        self._command_timeout_seconds = command_timeout_seconds
        self._sequence = 0

    def run(
        self,
        purpose: str,
        command: Sequence[str],
        *,
        check: bool = True,
        environment: Mapping[str, str] | None = None,
    ) -> tuple[ProofCommandRecord, str]:
        self._sequence += 1
        started = datetime.now(UTC)
        started_monotonic = time.monotonic()
        process_environment = os.environ.copy()
        if environment is not None:
            process_environment.update(environment)

        timed_out = False
        try:
            completed = subprocess.run(
                list(command),
                cwd=self._repository_root,
                env=process_environment,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                check=False,
                timeout=self._command_timeout_seconds,
            )
            exit_code = completed.returncode
            output = completed.stdout or ""
        except subprocess.TimeoutExpired as exception:
            timed_out = True
            exit_code = 124
            output = timeout_output(exception)
            output += (
                "\nRepository proof terminated this command after "
                f"{self._command_timeout_seconds} seconds.\n"
            )

        finished = datetime.now(UTC)
        safe_purpose = re.sub(r"[^a-z0-9]+", "-", purpose.lower()).strip("-")
        log_path = self._results_directory / (
            f"{self._sequence:02d}-{safe_purpose or 'command'}.log"
        )
        log_path.write_text(output, encoding="utf-8")
        restrict_file_permissions(log_path)
        record = ProofCommandRecord(
            purpose=purpose,
            command=tuple(command),
            started_at_utc=started.isoformat(),
            finished_at_utc=finished.isoformat(),
            duration_seconds=round(time.monotonic() - started_monotonic, 6),
            exit_code=exit_code,
            timed_out=timed_out,
            log_path=str(log_path.relative_to(self._repository_root)),
            log_sha256=hashlib.sha256(output.encode("utf-8")).hexdigest(),
        )
        if check and exit_code != 0:
            reason = "timed out" if timed_out else f"failed with exit code {exit_code}"
            raise RepositoryProofError(
                f"Command '{purpose}' {reason}. Inspect '{record.log_path}'."
            )
        return record, output


def timeout_output(exception: subprocess.TimeoutExpired) -> str:
    value = exception.stdout
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode("utf-8", errors="replace")
    return value


def restrict_file_permissions(path: Path) -> None:
    try:
        path.chmod(0o600)
    except OSError:
        # Windows ACLs and some mounted filesystems do not implement POSIX modes.
        pass


def find_repository_root(explicit: str | None, marker: str) -> Path:
    if explicit is not None:
        candidate = Path(explicit).expanduser().resolve()
        if not (candidate / marker).is_file():
            raise RepositoryProofError(
                f"Repository root '{candidate}' does not contain {marker}."
            )
        return candidate

    candidate = Path(__file__).resolve().parent
    while candidate != candidate.parent:
        if (candidate / marker).is_file():
            return candidate
        candidate = candidate.parent
    raise RepositoryProofError(
        f"Could not locate a repository root containing {marker}."
    )


def require_repository_path(
    repository_root: Path,
    value: str,
    description: str,
) -> Path:
    candidate = Path(value).expanduser()
    if not candidate.is_absolute():
        candidate = repository_root / candidate
    resolved = candidate.resolve()
    if not resolved.is_relative_to(repository_root):
        raise RepositoryProofError(
            f"{description} '{resolved}' must remain inside repository '{repository_root}'."
        )
    return resolved


def require_bounded_integer(
    value: int,
    minimum: int,
    maximum: int,
    description: str,
) -> int:
    if value < minimum or value > maximum:
        raise RepositoryProofError(
            f"{description} must be between {minimum} and {maximum}."
        )
    return value


def make_compose_project_name(prefix: str) -> str:
    normalized_prefix = re.sub(r"[^a-z0-9]+", "-", prefix.lower()).strip("-")
    timestamp = datetime.now(UTC).strftime("%Y%m%d%H%M%S%f")
    project_name = f"{normalized_prefix}-{timestamp}-{os.getpid()}"
    if not PROJECT_NAME_PATTERN.fullmatch(project_name):
        raise RepositoryProofError(
            f"Generated Compose project name '{project_name}' is invalid."
        )
    return project_name


def compose_prefix(
    compose_file: Path,
    environment_file: Path,
    project_name: str,
) -> list[str]:
    return [
        "docker",
        "compose",
        "--project-name",
        project_name,
        "--file",
        str(compose_file),
        "--env-file",
        str(environment_file),
    ]


def read_source_identity(
    repository_root: Path,
    *,
    allow_dirty: bool,
) -> RepositorySourceIdentity:
    commit = _run_git(repository_root, "rev-parse", "HEAD").strip().lower()
    if not GIT_COMMIT_PATTERN.fullmatch(commit):
        raise RepositoryProofError(
            f"Git returned invalid source commit identity '{commit}'."
        )
    status = _run_git(
        repository_root,
        "status",
        "--porcelain=v1",
        "--untracked-files=all",
    )
    tree_clean = status.strip() == ""
    if not tree_clean and not allow_dirty:
        raise RepositoryProofError(
            "Repository tree is dirty. Commit or remove every change before release proof, "
            "or use the explicit diagnostic --allow-dirty override."
        )
    return RepositorySourceIdentity(commit_sha=commit, tree_clean=tree_clean)


def _run_git(repository_root: Path, *arguments: str) -> str:
    try:
        completed = subprocess.run(
            ["git", *arguments],
            cwd=repository_root,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
            timeout=30,
        )
    except (OSError, subprocess.TimeoutExpired) as exception:
        raise RepositoryProofError(
            f"Could not read repository source identity: {exception}"
        ) from exception
    if completed.returncode != 0:
        raise RepositoryProofError(
            "Could not read repository source identity: " + (completed.stdout or "").strip()
        )
    return completed.stdout or ""


def write_json_report(path: Path, value: Any) -> None:
    payload = asdict(value) if is_dataclass(value) else value
    path.write_text(
        json.dumps(payload, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    restrict_file_permissions(path)
