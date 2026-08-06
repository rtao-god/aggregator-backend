# Implementation status

This document records implemented production owners and exact remaining work. An unchecked item has no placeholder endpoint or fake contract.

## Implemented foundation

- [x] .NET 10/C# 14 repository policy and central package versions.
- [x] Physical BuildingBlocks projects with no business-domain ownership.
- [x] Typed owner error and correlation middleware.
- [x] UUIDv7/UTC technical owners.
- [x] Checksum-verified, owner-scoped PostgreSQL migration runner.
- [x] Integration event envelope, exact-payload durable outbox/inbox primitives, bounded attempts, and dead-letter contracts.
- [x] S3-compatible object-store port and adapter.
- [x] OpenTelemetry and OIDC authorization bootstrap.
- [x] Explicit project-topology manifest, generated canonical solutions, forbidden-contour guards, and read-only CI foundation.
- [x] One canonical `compose.yaml`, `.env.example`, Caddy edge, SeaweedFS adapter, separate migration/grant jobs, container limits, healthchecks, and deployment topology guards.
- [x] Runtime-contract and Compose-config checks run before restore/build/test in `.github/workflows/ci.yml`.

## Catalog owner progress

- [x] Typed Catalog identifiers, lifecycle aggregates, immutable listing revisions, typed attribute values, field-level provenance, and publication state.
- [x] Product-configuration semantic validation and immutable activation contracts.
- [x] Catalog application use cases, idempotent commands, deterministic publication composition, and owner-level tests.
- [x] Normalized EF Core/PostgreSQL persistence, one-shot migration project, authenticated command API, S3 publication adapter, listing claims, and listing-scoped access persistence.
- [x] One canonical Catalog worker composition root using the shared durable outbox dispatcher.
- [x] Revisioned public-visibility suppression commands, atomic PostgreSQL history/outbox persistence, and stable media/contact target identities through publication artifacts.
- [ ] Merge media lifecycle into the canonical Catalog owner, bind listing revisions to exact approved media revisions/variants, and prove suppression/rollback behavior against PostgreSQL and object storage.

## Query owner progress

- [x] Query document, base projection, explicit empty overlays, and composite `PublicReadRevision` contracts.
- [x] Catalog publication event/artifact projection builder with exact identity and digest validation.
- [x] Canonical Promotion placement consumer, immutable Query-owned overlay materialization, hard-expiry enforcement, and atomic public-read revision switch.
- [x] PostgreSQL projection/read stores, Query migrations, one worker composition root, and public API returning sponsored and organic rows from one revision snapshot.
- [x] Block-first visibility-safety inbox, immutable overlay materialization, atomic `PublicReadRevision` switch, and listing/route/media/contact filtering.
- [x] Catalog publication recomposition preserves the exact current Promotion and safety overlays under a per-catalog mutation lease and blocks incompatible Promotion membership.
- [ ] Complete real PostgreSQL/RabbitMQ/object-storage migration, concurrency, failure-injection, and E2E proof for visibility safety.

## Ingestion owner progress

- [x] Backend-owned `aggregator-candidate-ingestion` manifest and package/item contracts.
- [x] Import-batch lifecycle, optimistic concurrency, terminal integrity failures, and immutable item-decision supersession.
- [x] Canonical package hashing, fail-closed package integrity, explicit accepted/review/rejected item decisions, producer authorization, and exact Catalog configuration projection validation.
- [x] Ingestion-only EF Core/PostgreSQL model, one-shot migration command, canonical API/worker composition roots, object-upload contracts, and strict JSON boundary.
- [ ] Complete review/commit selection, Catalog command/outcome ledger, generated collector client, resume behavior, PostgreSQL/object-storage proof, and production-path collector fixture delivery.

## Analytics owner progress

- [x] Typed interaction contracts, authenticated API, persistence/application owners, worker/migrations, privacy/quality classification, and aggregate-read contracts.
- [ ] Real PostgreSQL/RabbitMQ integration, closed-window aggregate proof, retention/privacy operations, and production-path E2E.

## Promotion owner progress

- [x] Product, entitlement, placement, capacity, hard-expiry, API, persistence, migrations, worker, and `promotion.placement.changed` producer contract.
- [x] Promotion-owned public overlay contour removed; Query is the sole materialized public-overlay owner.
- [ ] Real PostgreSQL/RabbitMQ schedule/outbox integration and production-path E2E through Query.

## Context completion

A context is checked only after its real Domain/Application/Infrastructure/API/Worker/Migrations path is committed and covered by required integration and E2E proof.

- [ ] Catalog.
- [ ] Query.
- [ ] Ingestion.
- [ ] Analytics.
- [ ] Promotion.

## Release proof not yet complete

- Berlin product configuration artifacts, schema validation, import, and explicit activation proof.
- Clean Docker image build, migration/grant execution, startup, health, and Compose smoke on a Docker host.
- Generated OpenAPI/JSON Schema/client artifacts and drift checks.
- Real PostgreSQL/PostGIS/RabbitMQ/object-storage integration tests and fresh/upgrade migration tests.
- Two production-path E2E scenarios without direct owner bypasses.
- Broader test-discovery guard beyond the PostgreSQL/RabbitMQ integration paths now required by CI.
- Container build/scan, dependency/security audit, SBOM, backup/restore drill, load proof, and production runbooks.
