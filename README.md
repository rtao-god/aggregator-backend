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

## Core commands

```powershell
pwsh ./tools/repo.ps1 restore
pwsh ./tools/repo.ps1 build
pwsh ./tools/repo.ps1 test
pwsh ./tools/repo.ps1 architecture
pwsh ./tools/repo.ps1 compose-up
pwsh ./tools/repo.ps1 compose-down
```

Database schema changes are applied only by the context-specific migration executables. API and worker startup never applies migrations.

## Current implementation status

The checked-in implementation is tracked in [`docs/decisions/implementation-status.md`](docs/decisions/implementation-status.md). Deferred product decisions do not receive placeholder services, endpoints, or fake contracts.
