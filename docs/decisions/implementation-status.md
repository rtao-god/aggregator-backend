# Implementation status

This document records implemented production owners and exact remaining work. An unchecked item has no placeholder endpoint or fake contract.

## Implemented foundation

- [x] .NET 10/C# 14 repository policy and central package versions.
- [x] Physical BuildingBlocks projects with no business-domain ownership.
- [x] Typed owner error and correlation middleware.
- [x] UUIDv7/UTC technical owners.
- [x] Checksum-verified, owner-scoped PostgreSQL migration runner.
- [x] Integration event envelope, durable outbox dispatch, bounded attempts, and dead-letter contracts.
- [x] S3-compatible object-store port and adapter.
- [x] OpenTelemetry and OIDC authorization bootstrap.
- [x] Architecture dependency tests and CI foundation.

## Catalog owner progress

- [x] Typed Catalog identifiers, lifecycle aggregates, immutable listing revisions, typed attribute values, field-level provenance, and publication state.
- [x] Product-configuration semantic validation and immutable activation contracts.
- [x] Catalog application use cases, idempotent commands, deterministic publication composition, and owner-level tests.
- [x] Normalized EF Core/PostgreSQL persistence, one-shot migration project, authenticated command API, S3 publication adapter, listing claims, and listing-scoped access persistence.
- [ ] Catalog worker composition root, media processing lifecycle, visibility suppression, complete rollback safety gates, and PostgreSQL/S3/RabbitMQ integration proof.

## Query owner progress

- [x] Query document, base projection, explicit empty overlays, and composite PublicReadRevision domain contracts.
- [x] Catalog publication event/artifact projection builder with exact identity and digest validation.
- [x] Revision-bound cursor and public-read application ports with domain/application proof.
- [ ] Query PostgreSQL projection store, worker/API composition roots, search/facets/SEO, promotion and safety overlays, migrations, and integration proof.

## Ingestion owner progress

- [x] Backend-owned `aggregator-candidate-ingestion` manifest and item contract.
- [x] Import-batch lifecycle, optimistic concurrency, terminal integrity failures, and immutable item-decision supersession.
- [x] Canonical package hashing, fail-closed package integrity, explicit accepted/review/rejected item decisions, producer authorization, and exact Catalog configuration projection validation.
- [ ] Ingestion PostgreSQL persistence, upload/object verification, API/worker composition roots, review and commit workflow, Catalog command delivery ledger, migrations, generated contract artifacts, and integration proof.

## Context completion

A context is checked only after its real Domain/Application/Infrastructure/API/Worker/Migrations path is committed and covered by tests.

- [ ] Catalog.
- [ ] Query.
- [ ] Ingestion.
- [ ] Analytics.
- [ ] Promotion.

## Release proof not yet complete

- Berlin product configuration artifacts, schema validation, import, and explicit activation proof.
- Complete database schemas and migrations for all five contexts.
- Generated OpenAPI/JSON Schema/client artifacts and drift checks.
- RabbitMQ topology and end-to-end event delivery.
- Docker Compose runtime and context-specific images.
- PostgreSQL/PostGIS/S3/RabbitMQ integration tests.
- Publication, projection, ingestion, analytics, and promotion end-to-end scenarios.
- Backup/restore drill, security suite, load proof, and production runbooks.
