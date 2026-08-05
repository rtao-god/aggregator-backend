# Aggregator Backend

Service-oriented .NET backend for configurable vertical catalogs. The repository owns public catalog meaning, editorial state, immutable publication, query projections, collector ingestion, interaction analytics, and sponsored placement.

## Architecture

The source repository contains five physically separated bounded contexts:

- **Catalog** — product configuration, subjects, listings, revisions, provenance, claims, media, and publication.
- **Query** — rebuildable public read models, search, facets, routes, SEO, and the atomic public-read revision.
- **Ingestion** — backend-owned candidate import, package integrity, review, matching, and Catalog delivery ledger.
- **Analytics** — accepted interaction events, privacy/quality classification, and aggregates.
- **Promotion** — manual entitlements, sponsored placement schedules, and promotion projection events.

Contexts do not share a business database or `DbContext`. Cross-context communication is limited to producer-owned contracts, generated clients, immutable artifacts, and asynchronous messages.

## Prerequisites

- .NET SDK 10
- Docker Engine with Compose
- PowerShell 7 for repository commands
- Python 3 for deterministic solution/inventory validation

## Development feedback loop

Run the cheapest sufficient proof first:

```powershell
# Project/solution inventory, dependency boundaries, security and CI invariants.
pwsh ./tools/repo.ps1 preflight

# Compile only production projects.
pwsh ./tools/repo.ps1 build-runtime

# Compile or test one changed owner and its dependency graph.
pwsh ./tools/repo.ps1 build-project -Project src/Analytics/Analytics.Api/Analytics.Api.csproj
pwsh ./tools/repo.ps1 test-project -Project tests/Analytics/Analytics.Api.Tests/Analytics.Api.Tests.csproj

# Final repository proof.
pwsh ./tools/repo.ps1 test-all
```

`AggregatorBackend.slnx` is the only complete solution owner. `AggregatorBackend.Runtime.slnx` is generated from every project under `src/` and contains no tests. `.tools/complete-backend.py --check` blocks orphan projects, broken references, and stale solution files before a full restore.

Automatic CI is read-only and ordered to stop on cheap structural failures before the full solution restore/build/test. Full semantic formatting is explicit:

```powershell
pwsh ./tools/repo.ps1 format-check
pwsh ./tools/repo.ps1 format-full-check
```

## Local runtime

Image compilation is explicit and separate from startup:

```powershell
pwsh ./tools/repo.ps1 compose-build
pwsh ./tools/repo.ps1 compose-up
pwsh ./tools/repo.ps1 compose-up-runtime
pwsh ./tools/repo.ps1 compose-down
```

`compose-up` never rebuilds images. This keeps repeated local starts bounded by container and dependency readiness rather than by a repository-wide .NET publish.

Database schema changes are applied only by the context-specific migration executables. API and worker startup never applies migrations.

## Current implementation status

The checked-in implementation is tracked in [`docs/decisions/implementation-status.md`](docs/decisions/implementation-status.md). Deferred product decisions do not receive placeholder services, endpoints, or fake contracts.
