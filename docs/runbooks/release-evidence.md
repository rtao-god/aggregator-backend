# Release evidence index

## Purpose

A backend release decision must not combine reports from different commits, diagnostic runs, missing logs or an inferred “latest” directory. `tools/verify-release-evidence.py` accepts four explicit proof paths, revalidates every referenced artifact and writes one commit-bound evidence index.

Required proofs:

```text
source verification
migration two-pass proof
runtime smoke proof
backup/restore proof
```

Required schemas:

```text
aggregator-backend/source-verification-proof@1
aggregator-backend/migration-proof@1
aggregator-backend/runtime-smoke-proof@1
aggregator-backend/backup-restore-proof@1
```

Output schema:

```text
aggregator-backend/release-evidence-index@1
```

## Prerequisites

- The repository is checked out at the exact release commit.
- The repository tree is clean.
- All four reports were produced from that same commit.
- Every referenced log remains at the exact repository-local path recorded by its report.
- No report was produced with `--allow-dirty`, `--keep-project` or another diagnostic override that made `release_valid` false.

## Static self-test

```bash
python tools/verify-release-evidence.py --self-test
```

The self-test verifies:

- exact log digest acceptance;
- tampered-log rejection;
- diagnostic-proof rejection;
- mixed-commit rejection.

It does not select or read repository proof reports.

## Verification

Every report path is mandatory and explicit:

```bash
python tools/verify-release-evidence.py \
  --source-report artifacts/source-verification-proof/<exact-id>/source-verification-proof.json \
  --migration-report artifacts/migration-proof/<exact-id>/migration-proof.json \
  --runtime-smoke-report artifacts/runtime-smoke-proof/<exact-id>/runtime-smoke-proof.json \
  --backup-restore-report artifacts/backup-restore-proof/<exact-id>/backup-restore-proof.json
```

Windows PowerShell:

```powershell
./tools/verify-release-evidence.ps1 `
  --source-report artifacts/source-verification-proof/<exact-id>/source-verification-proof.json `
  --migration-report artifacts/migration-proof/<exact-id>/migration-proof.json `
  --runtime-smoke-report artifacts/runtime-smoke-proof/<exact-id>/runtime-smoke-proof.json `
  --backup-restore-report artifacts/backup-restore-proof/<exact-id>/backup-restore-proof.json
```

The verifier intentionally has no default report paths, directory scan, glob or chronological selection. Operators must copy the exact report paths from the proof commands that were run for the release commit.

## Validation

### Common rules

Each input must have:

```text
expected schema identity
status = passed
release_valid = true
source_tree_clean = true
allow_dirty = false
source_commit = current clean HEAD
failure = null
```

### Command evidence

Every required command record is revalidated:

```text
exit_code = 0
timed_out = false
repository-local log exists
SHA-256(actual log bytes) = recorded log_sha256
```

An edited, removed or relocated command log invalidates the proof.

### Source proof

The verifier requires successful evidence for:

```text
contract verifier
solution build
guarded tests
```

It also rehashes the current:

```text
tools/verify-contracts.py
tools/run-tests-with-discovery-guard.py
AggregatorBackend.slnx
```

These hashes must equal the source-proof report.

### Migration proof

The verifier requires exactly:

```text
catalog passes 1 and 2
query passes 1 and 2
ingestion passes 1 and 2
analytics passes 1 and 2
promotion passes 1 and 2
```

Every service identity must be `<context>-migrate`, and isolated cleanup must have succeeded.

### Runtime proof

The verifier requires:

- five successful migration commands;
- resolved image evidence with no untagged or `:latest` image;
- one exact container for every canonical API/worker/reverse-proxy deployable;
- all containers in `running` state;
- all five API containers in `healthy` state;
- successful isolated cleanup.

### Backup/restore proof

The verifier rehashes current canonical owner scripts:

```text
tools/backup.sh
tools/restore.sh
tools/restore-proof.sh
```

The proof command must reference `tools/restore-proof.sh`, and every delegated argument in the JSON command evidence must remain redacted.

## Evidence output

A timestamped index is written under:

```text
artifacts/release-evidence/<UTC timestamp with microseconds>/release-evidence.json
```

It records:

- exact release commit;
- clean-tree state;
- `status: passed`;
- `release_valid: true`;
- each proof kind;
- each exact repository-local report path;
- SHA-256 of every complete report file;
- every report schema identity.

The index does not copy or reinterpret domain results. It identifies the exact complete proof set used for the release decision.

## Failure handling

When verification fails:

1. Do not replace only the rejected file with a nearby timestamped artifact.
2. Identify whether the owner report, command log, source commit or canonical script changed.
3. Rerun the owning proof from the exact current commit.
4. Pass the newly printed explicit report path to a new verification command.
5. Retain the failed verifier output in CI logs; do not mutate an index into success.

## Acceptance

Repository release evidence is valid only when the verifier exits zero and produces an index with:

```json
{
  "schema_identity": "aggregator-backend/release-evidence-index@1",
  "status": "passed",
  "release_valid": true,
  "source_tree_clean": true
}
```

The index is necessary evidence selection, not a substitute for the four underlying proofs.
