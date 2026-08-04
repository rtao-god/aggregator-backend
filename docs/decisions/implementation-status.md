# Implementation status

This document records implemented production owners and exact remaining work. An unchecked item has no placeholder endpoint or fake contract.

## Implemented foundation

- [x] .NET 10/C# 14 repository policy and central package versions.
- [x] Physical BuildingBlocks projects with no business-domain ownership.
- [x] Typed owner error and correlation middleware.
- [x] UUIDv7/UTC technical owners.
- [x] Checksum-verified, owner-scoped PostgreSQL migration runner.
- [x] Integration event envelope and outbox dispatch contracts.
- [x] S3-compatible object-store port and adapter.
- [x] OpenTelemetry and OIDC authorization bootstrap.
- [x] Architecture dependency tests and CI foundation.

## Context implementation

The context sections are updated only when their real Domain/Application/Infrastructure/API/Worker/Migrations path is committed and covered by tests.

- [ ] Catalog.
- [ ] Query.
- [ ] Ingestion.
- [ ] Analytics.
- [ ] Promotion.

## Release proof not yet complete

- Product configuration import and activation.
- Full database schemas and context migrations.
- Generated OpenAPI/JSON Schema/client artifacts and drift checks.
- RabbitMQ topology and end-to-end event delivery.
- Docker Compose runtime and context-specific images.
- PostgreSQL/PostGIS/S3/RabbitMQ integration tests.
- Publication, projection, ingestion, analytics, and promotion end-to-end scenarios.
- Backup/restore drill, security suite, load proof, and production runbooks.
