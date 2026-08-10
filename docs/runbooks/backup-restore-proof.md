# Backup and restore proof evidence

## Purpose

`tools/restore-proof.sh` remains the sole owner of backup creation, restore execution and restored-state validation. The Python command in this runbook does not implement another backup workflow. It binds the existing owner to:

- an exact Git commit;
- a clean source tree;
- bounded execution;
- exact command output;
- canonical-script SHA-256 identities;
- a stable JSON evidence contract.

Canonical owner chain:

```text
tools/backup.sh
→ tools/restore.sh
→ tools/restore-proof.sh
→ tools/run-backup-restore-proof.py (evidence orchestration only)
```

## Prerequisites

- The prerequisites documented by `tools/restore-proof.sh`.
- Bash available on `PATH`, or an explicit absolute Bash-compatible shell path.
- Python 3.11 or newer.
- A committed, clean repository tree for release evidence.
- Required credentials supplied through environment variables, mounted secret files or the canonical script's existing non-secret argument contract.

Do not pass raw secret values as command-line arguments. Delegated argument values are redacted from JSON evidence, but the operating system process table and the canonical script's own output are outside that redaction boundary.

## Static self-test

```bash
python tools/run-backup-restore-proof.py --self-test
```

The self-test verifies:

- delegated `--` separator handling;
- bounded argument count and size;
- NUL rejection;
- evidence-command redaction.

It does not execute backup or restore operations.

## Full proof

Arguments after `--` are delegated verbatim to the existing `tools/restore-proof.sh` contract.

Linux/macOS:

```bash
python tools/run-backup-restore-proof.py -- \
  <restore-proof arguments>
```

Windows PowerShell with Git Bash or another Bash-compatible shell available:

```powershell
./tools/run-backup-restore-proof.ps1 -- \
  <restore-proof arguments>
```

Explicit shell path:

```bash
python tools/run-backup-restore-proof.py \
  --shell-command /usr/bin/bash \
  -- \
  <restore-proof arguments>
```

The orchestrator invokes exactly:

```text
<resolved Bash executable>
<repository>/tools/restore-proof.sh
<delegated arguments>
```

It does not invoke `pg_dump`, `pg_restore`, `psql`, object-storage tools or Docker directly.

## Execution bounds

Default timeout:

```text
3600 seconds
```

Explicit override:

```bash
python tools/run-backup-restore-proof.py \
  --command-timeout-seconds 7200 \
  -- \
  <restore-proof arguments>
```

Accepted range:

```text
60..14400 seconds
```

A timeout is retained as:

```json
{
  "exit_code": 124,
  "timed_out": true
}
```

and the proof fails.

## Source and owner identity

Before execution the orchestrator records:

```text
exact Git commit
source-tree cleanliness
SHA-256(tools/backup.sh)
SHA-256(tools/restore.sh)
SHA-256(tools/restore-proof.sh)
resolved shell executable
```

A dirty tree fails before the canonical script executes.

For local diagnosis only:

```bash
python tools/run-backup-restore-proof.py \
  --allow-dirty \
  -- \
  <restore-proof arguments>
```

A successful diagnostic run is reported as:

```json
{
  "status": "diagnostic",
  "release_valid": false
}
```

It is not release evidence.

## Delegated argument boundary

Delegated arguments are bounded:

```text
maximum arguments:       64
maximum characters each: 4096
maximum total UTF-8:     16384 bytes
```

NUL characters are rejected. JSON evidence records only the count and a redacted command shape:

```text
bash
tools/restore-proof.sh
<delegated-argument-redacted>
...
```

Actual argument values are never persisted by the Python evidence report.

## Evidence

A timestamped directory is written under:

```text
artifacts/backup-restore-proof/<UTC timestamp with microseconds>/
```

It contains:

```text
backup-restore-proof.json
01-canonical-backup-restore-proof.log
```

Schema identity:

```text
aggregator-backend/backup-restore-proof@1
```

The report contains:

- exact source commit and cleanliness;
- `passed`, `diagnostic` or `failed` status;
- `release_valid` flag;
- exact canonical owner paths and SHA-256 digests;
- resolved shell path;
- delegated argument count;
- bounded timeout;
- start/finish timestamps;
- redacted command evidence;
- exact combined-output log path and SHA-256;
- exit code and timeout state;
- explicit failure reason.

Report and log files receive owner-only POSIX permissions where supported. The result path must remain inside the repository.

## Failure handling

1. Open the exact report path printed by the orchestrator.
2. Inspect its referenced command log.
3. Follow the canonical `tools/restore-proof.sh` failure semantics; do not bypass or partially reproduce them.
4. Correct the owning backup, restore or validation script.
5. Commit the correction.
6. Start a new proof from the corrected commit.

Do not edit a failed report into a successful report and do not select an inferred “latest” backup artifact outside the canonical owner contract.

## Acceptance

Backup/restore is release-proven only when:

- `status` is `passed`;
- `release_valid` is `true`;
- source tree is clean;
- source commit equals the release commit;
- all three canonical script hashes match that commit;
- command exit code is zero;
- the command did not time out;
- the exact command log digest is present;
- the canonical proof itself confirms restored database and object-state integrity;
- migration proof, runtime smoke proof, guarded tests and contract verification pass independently.
