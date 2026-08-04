# Ingestion module

## Owner

Ingestion is the canonical backend owner of registered import packages, package integrity decisions, explicit item decisions, review-ready state, Catalog command delivery state, and the final outcome ledger after a collector export crosses the backend boundary.

It does not own collector crawling or `collector-candidate-export`, and it cannot publish Catalog content. The backend-owned wire contract is `aggregator-candidate-ingestion`; the collector repository consumes its generated client through a collector-side adapter.

## Projects

- `Ingestion.Domain`: import-batch lifecycle, optimistic concurrency, terminal failure states, and immutable item-decision supersession.
- `Ingestion.Contracts`: producer-owned manifest, item, provenance, typed value, quality, package transport, and API response contracts.
- `Ingestion.Application`: canonical serialization, fail-closed package validation, producer authorization, exact Catalog-reference validation, idempotent registration, upload lifecycle orchestration, read-only batch queries, transport mapping, and storage ports.
- `Ingestion.Infrastructure`: the Ingestion-only EF Core model, atomic registration/lifecycle repositories, verified quarantine object-store adapter, producer registry, local Catalog-reference projection reader, UUIDv7/UTC adapters, and read-only database readiness.
- `Ingestion.Api`: authenticated manifest registration, scoped payload-upload authorization/completion, exact batch read, typed transport/model/auth failures, rate limiting, read-only health, and development-only protected OpenAPI.
- `Ingestion.Migrations`: one-shot SQL owner for Ingestion schemas, constraints, indexes, immutable package triggers, and migration identity.

The worker project is added only together with its first real package-validation or Catalog-delivery composition root; no placeholder host is retained.

## Active flow

```text
collector-owned sealed export
→ generated backend ingestion client
→ POST /api/ingestion/batches with exact manifest digest and Idempotency-Key
→ internal service authentication and producer/target Catalog validation
→ one Ingestion transaction stores batch + manifest + source policies + artifacts + exact command-result document
→ POST /api/ingestion/batches/{batchId}/upload-request creates an exact short-lived quarantine write authorization
→ collector uploads the registered object only to ingestion/quarantine/
→ POST /api/ingestion/batches/{batchId}/upload-complete verifies key, size, content type and SHA-256 through IObjectStore
→ lifecycle transaction records the exact uploaded-object proof
→ GET /api/ingestion/batches/{batchId} reads only the stored Ingestion projection
→ package-level integrity validation
→ explicit accepted / needs-review / rejected item decisions
→ review and selected commit
→ typed Catalog commands
→ exact Catalog outcomes in the Ingestion delivery ledger
```

## API boundary

- Audience: `aggregator-ingestion`.
- Registration/upload scope: `ingestion.upload`.
- Read scope: `ingestion.read`.
- Contract document scope: `ingestion.test-contracts` in Development only.
- Registration and each mutating upload command require one exact `Idempotency-Key` and an authenticated OIDC `sub` representing the calling workload identity.
- New registration returns `201`; exact idempotent replay returns `200` with `replayed = true` and the original command result, even if the mutable batch lifecycle has advanced afterwards.
- Upload authorization is restricted to `application/json`, the registered object key, the package size ceiling, and a bounded one-to-fifteen-minute lifetime.
- Upload completion does not trust the client: it verifies object metadata and opens the object through digest-verifying storage before changing the batch lifecycle.
- Missing batch is a typed `404`, not a successful empty payload.
- Numeric enum tokens are rejected; the generated string-enum wire contract is authoritative.
- Authentication, authorization, model-state, application, and domain failures include owner, code, correlation ID, and required action.
- `/health/live` and `/health/ready` are read-only and never migrate, process packages, repair rows, or publish.

## Persistence boundary

`ingestion_db` owns independent schemas used by the current registration and upload path:

- `contracts`: authorized collector producers and supported backend ingestion revisions;
- `catalog_projection`: minimal producer-owned Catalog identity/configuration projection consumed locally;
- `batches`: immutable manifest, source-policy and artifact rows plus mutable batch lifecycle state and exact payload-object identity;
- `operations`: immutable command request identity plus the exact canonical result document and digest returned by that command.

The app role has no `catalog_db` credentials. Business migrations run only through `Ingestion.Migrations` with `INGESTION_MIGRATOR_CONNECTION_STRING`; API or worker startup never migrates. Object storage is consumed only through `Platform.ObjectStorage.IObjectStore`; the Ingestion adapter owns the `ingestion/quarantine/` prefix and package-specific policy.

## Invariants

- Unknown contract revisions, manifest digest mismatches, count mismatches, duplicate item keys, item digest mismatches, item-index mismatches, and payload digest mismatches fail the complete package.
- `research_only` and `forbidden` source policies cannot authorize a production package.
- `link_only` provenance may support only an external-reference field.
- An item is never silently skipped; validation produces an explicit accepted, needs-review, or rejected decision with reason codes.
- Validation helpers consume set semantics only. The classification owner creates ordinal `SortedSet` collectors so combined reason codes are deterministic without making every helper own collection ordering.
- Package and item identities are exact and immutable; same semantic command identity with a different request digest is a conflict.
- Registration or lifecycle replay requires the exact `scope + key + request digest`; the same key with a different digest fails with an explicit conflict.
- Every replay verifies the immutable result-document digest and returns that stored result, not a later mutable batch projection.
- Producer plus collector-export identity is unique and cannot create a second batch under another idempotency key.
- Batch, canonical manifest, source policies, artifacts and idempotency result are committed atomically under serializable isolation.
- Manifest, policy, artifact and command rows reject update/delete in PostgreSQL; lifecycle changes belong only to the batch row and later owner workflows.
- A batch cannot enter the uploaded state until the exact registered object key, size, content type and digest have been verified.
- Object keys outside `ingestion/quarantine/`, traversal forms, unsupported content types and divergent storage expiry are rejected by the owner adapter.
- The target Site, Catalog, and active Catalog configuration revision come from an Ingestion-local projection of producer-owned Catalog events. Ingestion never reads `catalog_db`.
- Registration and upload create no Catalog subject, draft, or publication and perform no cross-database work.

## Proof

- Domain tests cover package-level failure states, exact decision coverage, partial Catalog outcomes, immutable item-decision supersession, and stale aggregate revisions.
- Application tests cover canonical package integrity, duplicate item rejection, post-seal mutation, explicit review/rejection decisions, unknown wire revisions, producer authorization, exact target Catalog configuration identity, every batch-state transport mapping, absent/existing read semantics, upload command idempotency, verified completion and canonical result-document round trips.
- Infrastructure tests inspect the Npgsql EF design model for concurrency, semantic uniqueness, restrictive foreign keys, idempotency ownership, exact result-document storage, required dedicated configuration, quarantine-key restrictions and verified object-store interaction.
- API tests cover anonymous and wrong-scope denial, missing workload subject, missing idempotency, numeric-enum rejection, create/replay, upload authorization/completion, exact read, typed missing state, and anonymous read-only liveness.
- Architecture tests enforce context project boundaries after the projects are included in the solution.
- PostgreSQL runtime migration/transaction tests and real OIDC/S3-compatible integration remain part of the integration stage; static and in-memory API proof is not reported as those runtime proofs.
