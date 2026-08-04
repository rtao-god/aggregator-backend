# Catalog module

## Owner

Catalog is the canonical owner of product configuration, listing identities and immutable revisions, accepted provenance, editorial decisions, listing-scoped claims/access, immutable publications, and the current Catalog publication pointer.

## Projects

- `Catalog.Domain`: owner invariants and state transitions; no framework dependencies.
- `Catalog.Contracts`: producer-owned wire contracts, publication artifact, and integration events.
- `Catalog.Application`: use cases, deterministic serialization, explicit mapping, ports, and typed failure translation.
- `Catalog.Infrastructure`: EF Core/PostgreSQL persistence, S3-compatible artifact adapter, and read-only readiness.
- `Catalog.Api`: authenticated command transport only; thin Controllers, no repository access or domain decisions.
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
→ atomic publication pointer switch + outbox
→ exact rollback by publication ID
```

## API boundary

`Catalog.Api` accepts only `Catalog.Contracts`, resolves an explicit `actor_id` projection from the authenticated principal, requires operation-specific OAuth scopes, rejects numeric enum tokens, and translates known owner failures into RFC 7807 responses. Missing actor mapping fails closed; it never provisions an Actor lazily.

## Proof

- Catalog domain invariant tests cover invalid subject kinds, forbidden provenance, optimistic concurrency, and scoped access revocation.
- Catalog application E2E covers configuration import/activation, listing revisions, approval, two deterministic publications, and exact rollback.
- Catalog API contract tests cover anonymous liveness, authentication, actor mapping, route/body identity mismatch, and enum wire rejection.
- Architecture tests block forbidden project references and business ownership in BuildingBlocks.
