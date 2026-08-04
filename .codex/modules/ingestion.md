# Ingestion module

## Owner

Ingestion is the canonical backend owner of registered import packages, package integrity decisions, explicit item decisions, review-ready state, Catalog command delivery state, and the final outcome ledger after a collector export crosses the backend boundary.

It does not own collector crawling or `collector-candidate-export`, and it cannot publish Catalog content. The backend-owned wire contract is `aggregator-candidate-ingestion`; the collector repository consumes its generated client through a collector-side adapter.

## Projects

- `Ingestion.Domain`: import-batch lifecycle, optimistic concurrency, terminal failure states, and immutable item-decision supersession.
- `Ingestion.Contracts`: producer-owned manifest, item, provenance, typed value, quality, and package transport contracts.
- `Ingestion.Application`: canonical serialization, fail-closed package validation, producer authorization, exact Catalog-reference validation, idempotent registration, and storage ports.
- `Ingestion.Infrastructure`: the Ingestion-only EF Core model, atomic registration repository, producer registry, local Catalog-reference projection reader, UUIDv7/UTC adapters, and read-only database readiness.
- `Ingestion.Migrations`: one-shot SQL owner for Ingestion schemas, constraints, indexes, immutable package triggers, and migration identity.

API and worker projects are added only together with their first real production composition root; no placeholder host is retained.

## Active flow

```text
collector-owned sealed export
→ generated backend ingestion client
→ exact manifest registration
→ producer and target Catalog validation
→ one Ingestion transaction stores batch + manifest + source policies + artifacts + idempotency result
→ uploaded-object identity verification
→ package-level integrity validation
→ explicit accepted / needs-review / rejected item decisions
→ review and selected commit
→ typed Catalog commands
→ exact Catalog outcomes in the Ingestion delivery ledger
```

## Persistence boundary

`ingestion_db` owns independent schemas used by the current registration path:

- `contracts`: authorized collector producers and supported backend ingestion revisions;
- `catalog_projection`: minimal producer-owned Catalog identity/configuration projection consumed locally;
- `batches`: immutable manifest, source-policy and artifact rows plus mutable batch lifecycle state;
- `operations`: immutable command idempotency results.

The app role has no `catalog_db` credentials. Business migrations run only through `Ingestion.Migrations` with `INGESTION_MIGRATOR_CONNECTION_STRING`; API or worker startup never migrates.

## Invariants

- Unknown contract revisions, manifest digest mismatches, count mismatches, duplicate item keys, item digest mismatches, item-index mismatches, and payload digest mismatches fail the complete package.
- `research_only` and `forbidden` source policies cannot authorize a production package.
- `link_only` provenance may support only an external-reference field.
- An item is never silently skipped; validation produces an explicit accepted, needs-review, or rejected decision with reason codes.
- Validation helpers consume set semantics only. The classification owner creates ordinal `SortedSet` collectors so combined reason codes are deterministic without making every helper own collection ordering.
- Package and item identities are exact and immutable; same semantic command identity with a different request digest is a conflict.
- Registration replay requires the exact `scope + key + request digest`; the same key with a different digest fails with an explicit conflict.
- Producer plus collector-export identity is unique and cannot create a second batch under another idempotency key.
- Batch, canonical manifest, source policies, artifacts and idempotency result are committed atomically under serializable isolation.
- Manifest, policy, artifact and command rows reject update/delete in PostgreSQL; lifecycle changes belong only to the batch row and later owner workflows.
- The target Site, Catalog, and active Catalog configuration revision come from an Ingestion-local projection of producer-owned Catalog events. Ingestion never reads `catalog_db`.
- Registration creates no Catalog subject, draft, or publication and performs no cross-database work.

## Proof

- Domain tests cover package-level failure states, exact decision coverage, partial Catalog outcomes, immutable item-decision supersession, and stale aggregate revisions.
- Application tests cover canonical package integrity, duplicate item rejection, post-seal mutation, explicit review/rejection decisions, unknown wire revisions, producer authorization, and exact target Catalog configuration identity.
- Infrastructure tests inspect the Npgsql EF model for concurrency, semantic uniqueness, restrictive foreign keys, idempotency ownership, and required dedicated configuration.
- Architecture tests enforce context project boundaries after the projects are included in the solution.
- PostgreSQL runtime migration and transaction tests remain part of the integration stage; static model proof is not reported as runtime database proof.
