# Catalog module

## Owner

Catalog is the canonical owner of product configuration, listing identities and immutable revisions, accepted provenance, editorial decisions, listing-scoped claims/access, immutable publications, and the current Catalog publication pointer.

## Projects

- `Catalog.Domain`: owner invariants and state transitions; no framework dependencies.
- `Catalog.Contracts`: producer-owned wire contracts, publication artifact, and integration events.
- `Catalog.Application`: use cases, deterministic serialization, explicit mapping, ports, typed failure translation, event correlation, and canonical payload digests.
- `Catalog.Infrastructure`: EF Core/PostgreSQL persistence, durable outbox rows, S3-compatible artifact adapter, and read-only readiness.
- `Catalog.Api`: authenticated command transport only; thin Controllers, no repository access or domain decisions.
- `Catalog.Worker`: fail-fast outbox dispatcher; it has no HTTP surface and never applies migrations.
- `Catalog.Migrations`: one-shot owner migrations; API/worker startup never migrates.

## Active flows

```text
validated product configuration artifact
→ explicit import
→ explicit activation
→ listing identity
→ immutable listing revision
→ editorial approval
→ deterministic publication artifact
→ verified object storage write
→ atomic publication pointer switch + correlated outbox event
→ bounded Catalog worker lease
→ publisher-confirmed RabbitMQ delivery or explicit dead-letter state
→ exact rollback by publication ID
```

## API boundary

`Catalog.Api` accepts only `Catalog.Contracts`, resolves an explicit `actor_id` projection from the authenticated principal, requires operation-specific OAuth scopes, rejects numeric enum tokens, and translates known owner failures into RFC 7807 responses. Missing actor mapping fails closed; it never provisions an Actor lazily. HTTP correlation is propagated into every resulting integration event.

## Messaging boundary

Catalog persists business state and the producer-owned event envelope in one PostgreSQL transaction. The row carries the exact routing key, contract identity, canonical payload digest, correlation/causation, delivery attempts, lease state, dispatch completion, and dead-letter state. Migration from the legacy outbox fails closed when undelivered legacy rows exist because their missing digest and correlation cannot be reconstructed safely.

## Proof

- Catalog domain invariant tests cover invalid subject kinds, forbidden provenance, optimistic concurrency, and scoped access revocation.
- Catalog application E2E covers configuration import/activation, listing revisions, approval, two deterministic publications, and exact rollback.
- Catalog event tests cover canonical payload digest and explicit correlation/causation.
- Catalog API contract tests cover anonymous liveness, authentication, actor mapping, route/body identity mismatch, and enum wire rejection.
- Catalog worker tests cover strict required configuration and bounded transport settings.
- Architecture tests block forbidden project references and business ownership in BuildingBlocks.
