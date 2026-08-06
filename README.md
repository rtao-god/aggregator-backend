# Aggregator Backend

Service-oriented .NET backend for configurable vertical catalogs. The repository owns public catalog meaning, editorial state, immutable publication, backend candidate-package ingestion, interaction analytics, Query projections, and sponsored-placement lifecycle.

## Architecture

The source repository contains five physically separated bounded contexts:

- **Catalog** — product configuration, subjects, listings, revisions, provenance, claims, media, and publication.
- **Query** — rebuildable public read models, search, facets, routes, SEO, sponsored/organic composition, and the atomic public-read revision.
- **Ingestion** — backend-owned candidate package intake, integrity, review, matching, and Catalog delivery ledger.
- **Analytics** — accepted interaction events, privacy/quality classification, and aggregates.
- **Promotion** — manual entitlements, sponsored placement schedules, capacity, and producer events consumed by Query.

Contexts do not share a business database or `DbContext`. Cross-context communication is limited to producer-owned contracts, generated clients, immutable artifacts, and asynchronous messages. Crawling, source-specific extraction, collector evidence, and sealed collector exports remain outside this repository.

## Prerequisites

- .NET SDK 10
- Docker Engine with Compose v2
- PowerShell 7 for repository commands
- Python 3 for deterministic topology, contract, and solution validation

## Development feedback loop

Run the cheapest sufficient proof first:

```powershell
# Approved topology, generated contract guard, Compose config, architecture and CI invariants.
pwsh ./tools/repo.ps1 preflight

# Compile only approved production projects.
pwsh ./tools/repo.ps1 build-runtime

# Compile or test one changed owner and its dependency graph.
pwsh ./tools/repo.ps1 build-project -Project src/Analytics/Analytics.Api/Analytics.Api.csproj
pwsh ./tools/repo.ps1 test-project -Project tests/Analytics/Analytics.Api.Tests/Analytics.Api.Tests.csproj

# Final repository proof.
pwsh ./tools/repo.ps1 test-all
```

`docs/architecture/project-topology.json` is the only project-topology owner. `AggregatorBackend.slnx` contains exactly its approved project set; `AggregatorBackend.Runtime.slnx` contains exactly its approved production subset. `.tools/complete-backend.py --check` rejects missing, unknown, forbidden, or duplicate projects, broken `ProjectReference` edges, obsolete contour references, and stale generated solution/inventory files before restore.

Automatic CI is read-only and stops on repository inventory, runtime-contract, Compose-topology, and architecture failures before the full solution restore/build/test. `preflight` runs the same owner checks locally. Full semantic formatting is explicit:

```powershell
pwsh ./tools/repo.ps1 format-check
pwsh ./tools/repo.ps1 format-full-check
```

## Local runtime

`compose.yaml` is the only deployment graph. It exposes only the reverse proxy on loopback; databases, broker, object storage, scanner, APIs, workers, migrations, and grant jobs stay on the internal Docker network.

```powershell
Copy-Item .env.example .env
# Replace every CHANGE_ME value in .env.

pwsh ./tools/repo.ps1 compose-config
pwsh ./tools/repo.ps1 compose-build
pwsh ./tools/repo.ps1 db-migrate
pwsh ./tools/repo.ps1 compose-up
pwsh ./tools/repo.ps1 compose-down
```

Image compilation is explicit and separate from startup. `compose-up` uses `--no-build`; migrations and grants run in separate one-shot containers before runtime services. API and worker startup never applies DDL. The public local endpoint is `http://127.0.0.1:$BACKEND_HTTP_PORT`.

The checked-in implementation and remaining release proof are tracked in [`docs/decisions/implementation-status.md`](docs/decisions/implementation-status.md). A checked topology or build is not reported as runtime/E2E proof.
