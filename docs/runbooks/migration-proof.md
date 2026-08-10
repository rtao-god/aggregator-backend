# Database migration proof

## Purpose

This procedure verifies the production migration composition root of every bounded context without granting migration privileges to an API or worker process.

Canonical owners:

```text
catalog-migrate   → catalog_db
query-migrate     → query_db
ingestion-migrate → ingestion_db
analytics-migrate → analytics_db
promotion-migrate → promotion_db
```

The proof uses a unique Docker Compose project name. Its containers, networks and volumes are isolated from the normal development stack and are removed at the end unless diagnostic retention is explicitly requested.

## Prerequisites

- Docker Engine and Docker Compose with `config --format json` and `up --wait` support.
- Python 3.11 or newer.
- A repository-local `.env` created explicitly from `.env.example` with all required secrets populated.
- No reliance on an already-running database or application container.

Do not point this command at production credentials. It creates and removes the volumes belonging to its own generated Compose project.

## Static self-test

```bash
python tools/run-migration-proof.py --self-test
```

The self-test validates context normalization, unknown-context rejection, Compose JSON parsing and recursive dependency discovery. It does not start Docker.

## Full proof

From the repository root:

```bash
python tools/run-migration-proof.py --env-file .env
```

For each owner the runner performs:

```text
Compose config validation
→ recursive migration dependency discovery
→ isolated dependencies started with health waiting
→ first migration execution from an empty database
→ second execution against the already migrated database
→ exact log capture and SHA-256 digest
→ isolated project cleanup
```

The second pass proves that the migration host can inspect an already current database without attempting an unsafe startup migration or producing a second schema owner.

## Targeted proof

A subset can be selected without changing owner semantics:

```bash
python tools/run-migration-proof.py \
  --env-file .env \
  --contexts catalog query
```

Allowed context names are exactly:

```text
catalog
query
ingestion
analytics
promotion
```

Unknown names fail before Docker is invoked.

## Results

A timestamped directory is written under:

```text
artifacts/migration-proof/<UTC timestamp>/
```

It contains:

```text
migration-proof.json
NN-<command-purpose>.log
```

`migration-proof.json` has schema identity:

```text
aggregator-backend/migration-proof@1
```

The report records:

- exact Compose project identity;
- selected contexts;
- dependency services;
- every command and exit code;
- UTC start and finish timestamps;
- command duration;
- relative log path;
- SHA-256 of each command's exact combined output;
- cleanup result;
- explicit failure detail.

A command exit code of zero is not accepted unless all selected contexts have exactly two successful migration passes and isolated cleanup also succeeds.

## Failure handling

On failure:

1. Read the report path printed by the runner.
2. Inspect the referenced log rather than rerunning an inferred “latest” command.
3. Correct the owning migration project or Compose contract.
4. Start a new proof. Do not edit a failed report into a successful one.

To retain the isolated stack temporarily for diagnosis:

```bash
python tools/run-migration-proof.py \
  --env-file .env \
  --keep-project
```

The exact generated `compose_project_name` is recorded in the report. Cleanup must target that exact name:

```bash
docker compose \
  --project-name <exact compose_project_name> \
  --file compose.yaml \
  --env-file .env \
  down --volumes --remove-orphans
```

Never use an unscoped `docker compose down --volumes` as a recovery shortcut.

## Acceptance evidence

A migration owner is release-proven only when:

- the full five-context report has `status: passed`;
- both passes exist for every context;
- all command exit codes are zero;
- log digests are present;
- cleanup succeeded;
- the report was produced from the exact release commit;
- the normal build and guarded test suite also pass independently.

This runner does not replace backup/restore proof, RabbitMQ delivery proof, object-storage proof or application E2E scenarios.
