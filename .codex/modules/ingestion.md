# Ingestion module

## Owner

Ingestion is the canonical backend owner of registered import packages, package integrity decisions, explicit item decisions, review-ready state, Catalog command delivery state, and the final outcome ledger after a collector export crosses the backend boundary.

It does not own collector crawling or `collector-candidate-export`, and it cannot publish Catalog content. The backend-owned wire contract is `aggregator-candidate-ingestion`; the collector repository consumes its generated client through a collector-side adapter.

## Projects

- `Ingestion.Domain`: import-batch lifecycle, optimistic concurrency, terminal failure states, and immutable item-decision supersession.
- `Ingestion.Contracts`: producer-owned manifest, item, provenance, typed value, quality, and package transport contracts.
- `Ingestion.Application`: canonical serialization, fail-closed package validation, producer authorization, exact Catalog-reference validation, idempotent registration, and storage ports.

Infrastructure, API, worker, and migration projects are added only together with their first real production owner path; no placeholder composition roots are retained.

## Active flow

```text
collector-owned sealed export
→ generated backend ingestion client
→ exact manifest registration
→ producer and target Catalog validation
→ uploaded-object identity verification
→ package-level integrity validation
→ explicit accepted / needs-review / rejected item decisions
→ review and selected commit
→ typed Catalog commands
→ exact Catalog outcomes in the Ingestion delivery ledger
```

## Invariants

- Unknown contract revisions, manifest digest mismatches, count mismatches, duplicate item keys, item digest mismatches, item-index mismatches, and payload digest mismatches fail the complete package.
- `research_only` and `forbidden` source policies cannot authorize a production package.
- `link_only` provenance may support only an external-reference field.
- An item is never silently skipped; validation produces an explicit accepted, needs-review, or rejected decision with reason codes.
- Package and item identities are exact and immutable; same semantic command identity with a different request digest is a conflict.
- The target Site, Catalog, and active Catalog configuration revision come from an Ingestion-local projection of producer-owned Catalog events. Ingestion never reads `catalog_db`.
- Registration creates no Catalog subject, draft, or publication and performs no cross-database work.

## Proof

- Domain tests cover package-level failure states, exact decision coverage, partial Catalog outcomes, immutable item-decision supersession, and stale aggregate revisions.
- Application tests cover canonical package integrity, duplicate item rejection, post-seal mutation, explicit review/rejection decisions, unknown wire revisions, producer authorization, and exact target Catalog configuration identity.
- Architecture tests enforce context project boundaries after the projects are included in the solution.
