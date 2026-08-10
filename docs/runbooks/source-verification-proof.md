# Source verification proof

## Purpose

This proof binds the repository's existing contract verifier and test-discovery guard to an exact clean Git commit. It produces structured evidence for:

```text
contract drift verification
→ full solution compilation
→ guarded test discovery and execution
```

Canonical owners remain:

```text
tools/verify-contracts.py
tools/run-tests-with-discovery-guard.py
AggregatorBackend.slnx
```

`tools/run-source-verification-proof.py` is orchestration and evidence only. It does not duplicate contract rules or test-discovery semantics.

## Prerequisites

- .NET SDK pinned by `global.json`.
- Python 3.11 or newer.
- Restored NuGet dependencies.
- A committed, clean repository tree.

## Static self-test

```bash
python tools/run-source-verification-proof.py --self-test
```

The self-test validates canonical owner identities and timeout bounds. It does not build or test the solution.

## Full proof

Linux/macOS:

```bash
python tools/run-source-verification-proof.py
```

Windows PowerShell:

```powershell
./tools/run-source-verification-proof.ps1
```

Execution order is fixed:

```text
exact Git commit and clean-tree validation
→ python tools/verify-contracts.py
→ dotnet build AggregatorBackend.slnx /m:1 /nr:false --nologo
→ python tools/run-tests-with-discovery-guard.py
```

The proof never invokes raw `dotnet test`. All test execution passes through the repository's test-discovery guard, which rejects a successful process that discovered no required tests.

## Execution bounds

Default per-command timeout:

```text
1800 seconds
```

Override:

```bash
python tools/run-source-verification-proof.py \
  --command-timeout-seconds 3600
```

Accepted range:

```text
60..7200 seconds
```

A timed-out contract, build or test process is terminated as a complete process tree and retained with canonical exit code `124`.

## Evidence

A timestamped directory is written under:

```text
artifacts/source-verification-proof/<UTC timestamp with microseconds>/
```

It contains:

```text
source-verification-proof.json
01-verify-contracts.log
02-build-solution.log
03-run-guarded-tests.log
```

Schema identity:

```text
aggregator-backend/source-verification-proof@1
```

The report records:

- exact source commit;
- source-tree cleanliness;
- `passed`, `diagnostic` or `failed` status;
- `release_valid` flag;
- SHA-256 of the contract verifier;
- SHA-256 of the test-discovery guard;
- SHA-256 of the solution descriptor;
- Python executable identity;
- timeout bound;
- exact command evidence;
- log paths and SHA-256 digests;
- exit code and timeout state for every phase;
- explicit failure detail.

## Diagnostic dirty-tree override

For local diagnosis only:

```bash
python tools/run-source-verification-proof.py --allow-dirty
```

A successful dirty-tree run is reported as:

```json
{
  "status": "diagnostic",
  "release_valid": false
}
```

It must not be used for a release decision.

## Failure handling

1. Open the exact report path printed by the runner.
2. Inspect the failed phase's exact log.
3. Fix the owning contract rule, project or test.
4. Commit the correction.
5. Run a new proof from the corrected commit.

Do not skip the failed phase, run an unguarded subset as replacement or edit a failed report.

## Acceptance

Source verification is release-proven only when:

- `status` is `passed`;
- `release_valid` is `true`;
- source tree is clean;
- source commit equals the release commit;
- contract verification exits zero;
- solution build exits zero;
- guarded tests exit zero with valid discovery evidence;
- no phase timed out;
- all owner-script and log digests are present;
- migration, runtime smoke and backup/restore proofs pass independently.
