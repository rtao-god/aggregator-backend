# Local runtime runbook

## Bootstrap

```powershell
Copy-Item .env.example .env
```

Replace every `CHANGE_ME` value. `.env` is ignored by Git. Local credentials should be unique to this runtime; production secrets and image digests belong to the external deployment owner.

## Validate and build

```powershell
pwsh ./tools/repo.ps1 compose-config
pwsh ./tools/repo.ps1 compose-build
```

`compose-config` uses `.env.example` when `.env` does not exist, so the topology can be validated without real secrets. `compose-build` does not start or mutate runtime state.

## Apply schema and grants

```powershell
pwsh ./tools/repo.ps1 db-migrate
```

This starts infrastructure dependencies and executes each migration/grant owner. It does not start APIs or workers.

## Start

```powershell
pwsh ./tools/repo.ps1 compose-up
```

Startup uses existing images only. Compose waits for infrastructure health, successful migrations/grants, API readiness, worker liveness, and the Caddy edge. The local endpoint binds to loopback only.

## Inspect

```powershell
docker compose --env-file .env --file compose.yaml ps
docker compose --env-file .env --file compose.yaml logs --tail 200 <service>
```

A healthy process is not proof of business progress. Use context API state, outbox/inbox rows, worker metrics, and integration tests for owner-level diagnosis.

## Stop

```powershell
pwsh ./tools/repo.ps1 compose-down
```

Named volumes are retained. Destructive volume removal requires an explicit operator action and is not part of the repository command.
