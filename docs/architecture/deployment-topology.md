# Deployment topology

## Owner

`compose.yaml` is the only runtime topology owner. `.env.example` owns the documented local environment contract. `deploy/Dockerfile.dotnet-service` owns ordinary .NET images; `deploy/Dockerfile.catalog-media-worker` owns the ImageMagick-capable media worker image. `deploy/Caddyfile` owns local edge routing.

## Startup graph

```text
PostgreSQL / RabbitMQ / SeaweedFS / ClamAV
→ five bounded-context migration executables
→ context grant jobs
→ APIs and workers
→ Caddy loopback edge
```

Migrations and grants are one-shot processes. Runtime hosts do not apply DDL or repair schemas. Catalog media tables, publication gates, and media outbox state belong to the single `Catalog.Migrations` stream. Media HTTP commands run inside `catalog-command-api`; `catalog-media-worker` remains a separate resource-heavy Catalog worker and has no independent API, database, or migration owner.

## Data and network boundaries

- One PostgreSQL 18/PostGIS container owns five databases and separate app/migrator roles.
- Workers and APIs receive only their context app credentials.
- Only migration/grant jobs receive migrator credentials.
- RabbitMQ, SeaweedFS, ClamAV, databases, APIs, and workers have no host port.
- Caddy is the only edge and binds to `127.0.0.1`.
- The backend network is internal. APIs also join a non-published egress network only for external identity metadata; workers stay internal. The edge network is attached only to Caddy.
- SeaweedFS creates explicit buckets for Catalog artifacts, Catalog media, and Ingestion packages.

## Container contract

- Runtime .NET containers are non-root, read-only, capability-dropped, `no-new-privileges`, PID/memory/CPU bounded, and receive writable tmpfs only.
- API healthchecks call each service's read-only readiness endpoint through the repository-owned `HealthProbe` executable.
- Worker healthchecks prove the owned process remains PID 1; functional worker progress requires integration metrics/tests and is not inferred from process liveness.
- Image build and runtime base images are explicit variables. Local examples use exact tags; production must pin approved immutable digests.
- Startup never builds images. Build, migration, startup, and shutdown are separate repository commands.

## Structural proof

Repository preflight and CI run:

1. project topology validation;
2. runtime-contract manifest verification;
3. `docker compose ... config --quiet` against `.env.example`;
4. architecture tests that require one Compose file, two Dockerfile owners, exact approved deployable coverage, local-only exposure, and removal of obsolete deployment contours.

This proof validates configuration and dependency shape. Container build, clean startup, migration execution, runtime health, and E2E behavior remain separate required gates.
