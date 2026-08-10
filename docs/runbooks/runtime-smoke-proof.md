# Backend runtime smoke proof

## Purpose

This proof starts the complete backend topology from an exact committed source tree in a uniquely named Docker Compose project. It verifies migration deployables, runtime process state and API health without depending on the normal development stack.

Canonical migration deployables:

```text
catalog-migrate
query-migrate
ingestion-migrate
analytics-migrate
promotion-migrate
```

Canonical runtime deployables:

```text
catalog-api
catalog-worker
catalog-media-worker
query-api
query-worker
ingestion-api
ingestion-worker
analytics-api
analytics-worker
promotion-api
promotion-worker
reverse-proxy
```

## Prerequisites

- Docker Engine and modern Docker Compose.
- Python 3.11 or newer.
- A repository-local `.env` created explicitly from `.env.example`.
- A clean committed source tree.
- Sufficient local resources to start the complete topology.

Do not use production credentials. The runner removes only the generated Compose project's containers, networks and volumes.

## Static self-test

```bash
python tools/run-runtime-smoke-proof.py --self-test
```

The self-test does not start Docker. It verifies:

- the canonical service set;
- dependency closure;
- API healthcheck enforcement;
- Compose `ps` JSON and JSON-lines parsing;
- one-container-per-service evidence;
- mutable `latest` image detection.

## Full proof

Linux/macOS:

```bash
python tools/run-runtime-smoke-proof.py --env-file .env
```

Windows PowerShell:

```powershell
./tools/run-runtime-smoke-proof.ps1 --env-file .env
```

The execution order is fixed:

```text
exact source commit and clean-tree validation
→ Compose configuration validation without interpolation
→ dependency discovery
→ dependency startup with bounded health waiting
→ five one-shot migration deployables
→ all API and worker deployables
→ Compose health wait
→ bounded stability interval
→ exact Compose container-state capture
→ isolated project cleanup
```

Application hosts never run migrations during startup. The smoke command invokes the dedicated migration owners before starting runtime services.

## Readiness rules

Every canonical runtime service must have exactly one Compose container in `running` state.

The following API hosts must additionally report `healthy` through their Compose healthcheck:

```text
catalog-api
query-api
ingestion-api
analytics-api
promotion-api
```

Workers are required to remain running through the stability interval. They are not declared healthy merely because an HTTP endpoint elsewhere is healthy.

Default bounds:

```text
command timeout:  900 seconds
startup timeout:  300 seconds
stability window: 10 seconds
```

Explicit overrides:

```bash
python tools/run-runtime-smoke-proof.py \
  --env-file .env \
  --command-timeout-seconds 1200 \
  --startup-timeout-seconds 420 \
  --stability-seconds 20
```

Accepted ranges:

```text
command timeout: 30..7200 seconds
startup timeout: 30..1800 seconds
stability:       1..120 seconds
```

## Evidence

A timestamped directory is written under:

```text
artifacts/runtime-smoke-proof/<UTC timestamp with microseconds>/
```

It contains exact command logs and:

```text
runtime-smoke-proof.json
```

Schema identity:

```text
aggregator-backend/runtime-smoke-proof@1
```

The report records:

- exact source commit;
- source-tree cleanliness;
- Compose project identity;
- migration command evidence;
- runtime startup command evidence;
- API/worker container IDs;
- effective image identities reported by Compose;
- state and health;
- timeout bounds;
- stability interval;
- log paths and SHA-256 digests;
- diagnostics command on failure;
- cleanup result.

A zero exit code is not enough unless all five migration commands completed and all canonical runtime deployables have exact state evidence.

## Failure handling

When startup, health or state validation fails, the runner captures the latest 500 timestamped log lines for every canonical runtime service before cleanup.

Procedure:

1. Open the printed `runtime-smoke-proof.json` path.
2. Inspect the exact failed command log and diagnostic log.
3. Correct the owning deployable, migration or Compose contract.
4. Commit the correction.
5. Run a new proof from the corrected commit.

For temporary diagnosis only:

```bash
python tools/run-runtime-smoke-proof.py \
  --env-file .env \
  --keep-project
```

The report contains the exact `compose_project_name`. Cleanup must target that identity explicitly:

```bash
docker compose \
  --project-name <exact compose_project_name> \
  --file compose.yaml \
  --env-file .env \
  down --volumes --remove-orphans
```

Never clean an unscoped Compose project or reuse a report from another commit.

## Diagnostic dirty-tree override

`--allow-dirty` exists only to diagnose uncommitted local code. The report records the dirty state. Such evidence is not release-valid.

## Acceptance

Runtime topology is release-proven only when:

- report status is `passed`;
- source tree is clean;
- source commit equals the release commit;
- every migration deployable completed;
- every runtime deployable is running;
- every API host is healthy;
- the stability interval completed;
- no command timed out;
- isolated cleanup succeeded;
- guarded tests, migration two-pass proof, backup/restore proof and contract drift proof pass independently.
