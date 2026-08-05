# Deployment module

## Owner

- `compose.yaml`: one local runtime graph and startup dependency owner.
- `.env.example`: documented local configuration keys and exact example image tags.
- `deploy/Dockerfile.dotnet-service`: ordinary API, worker, and migration image contract.
- `deploy/Dockerfile.catalog-media-worker`: media worker image with ImageMagick.
- `deploy/entrypoint.sh`: validated assembly launch and PID 1 handoff.
- `deploy/postgres/init/001-create-context-databases.sh`: context database/role bootstrap.
- `deploy/postgres/grant-context.sh`: post-migration app grants through PG environment fields.
- `deploy/Caddyfile`: loopback reverse-proxy allowlist.
- `tools/repo.ps1`: build/migrate/start/stop commands.

## Invariants

- No Compose overlays or alternate active deployment graphs.
- Only Caddy exposes a host port, bound to `127.0.0.1`; API egress exists only for the external identity authority.
- One PostgreSQL container, five context databases, separate app/migrator roles, no worker migrator credentials.
- Migrations and grants are separate one-shot containers; runtime startup never performs DDL.
- Runtime containers are non-root, read-only, capability-dropped, resource bounded, and healthchecked.
- SeaweedFS is only the local S3-compatible adapter; no MinIO path remains.
- Unknown image tags, project paths, option sections, or route prefixes fail structural proof.
- Container config/build/start/smoke/E2E are distinct proof gates.
