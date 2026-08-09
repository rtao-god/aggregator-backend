# Ingestion module

Status: in development

## Owner

Ingestion is the canonical backend owner of registered import packages, uploaded-object integrity, package and item decisions, review state, selected commit state, Catalog-command delivery, and the exact Catalog outcome ledger after a collector export crosses the backend boundary.

It does not own collector crawling or `collector-candidate-export`, and it cannot publish Catalog content. The backend-owned wire contract is `aggregator-candidate-ingestion`; the collector repository consumes its generated client through a collector-side adapter.

## Projects

- `Ingestion.Domain`: import-batch lifecycle, optimistic concurrency, terminal failure states, and immutable item-decision supersession.
- `Ingestion.Contracts`: producer-owned manifest, candidate payload, provenance, typed value, quality, upload, processing, review, commit, delivery, and API response contracts.
- `Ingestion.Application`: canonical serialization, fail-closed package validation, producer authorization, exact Catalog-reference validation, idempotent registration/upload/commit orchestration, explicit review, Catalog-delivery orchestration, read-only queries, and storage ports.
- `Ingestion.Infrastructure`: Ingestion-only PostgreSQL models, atomic registration and lifecycle repositories, immutable processing decisions, delivery ledger, command-result persistence, verified quarantine object-store adapter, producer registry, local Catalog-reference projection reader, UUIDv7/UTC adapters, and read-only database readiness.
- `Ingestion.Api`: authenticated registration, upload authorization/completion, processing and Catalog-delivery ledger reads, review and commit commands, typed transport/model/auth failures, rate limiting, read-only health, and development-only protected OpenAPI.
- `Ingestion.Worker`: bounded validation and Catalog-delivery loops over canonical PostgreSQL leases. It owns no parallel payload, command, or outcome contract.
- `Ingestion.Migrations`: one-shot SQL owner for registration, canonical processing and delivery, lease/retry consistency, constraints, indexes, immutable records, lifecycle-transition enforcement, migration identity, and explicit removal of obsolete validation/delivery paths.

There is one active review/commit implementation: `IngestionProcessingContracts`, `IngestionProcessingServices`, `IngestionProcessingPersistence`, `IngestionProcessingController`, and `Ingestion.Worker`. The superseded parallel review/commit contracts, controller, workflow, tests, source generators, and source-mutating CI workflows were removed rather than retained as compatibility code. The historical migration that created the abandoned package-validation tables is followed by an owner migration that drops them.

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
→ validation worker leases the exact uploaded batch
→ exact payload bytes and package identity are verified
→ every item receives accepted / needs-review / rejected with reason codes
→ POST /api/ingestion/batches/{batchId}/review supersedes only exact review-required decisions
→ POST /api/ingestion/batches/{batchId}/commit creates one idempotent delivery per selected accepted item
→ the canonical delivery service leases typed Catalog draft commands only while the batch is committing
→ the worker sends the producer-owned command through an authenticated internal HTTP adapter
→ Catalog remains the final draft owner and cannot be bypassed
→ exact Catalog outcomes close the Ingestion delivery ledger
→ GET /api/ingestion/batches/{batchId}/deliveries reads the local ledger without Catalog credentials or calls
```

## API boundary

- Audience: `aggregator-ingestion`.
- Registration/upload scope: `ingestion.upload`.
- Read scope: `ingestion.read`.
- Review scope: `ingestion.review`.
- Commit scope: `ingestion.commit`.
- Contract document scope: `ingestion.test-contracts` in Development only.
- Registration, upload and commit commands require one exact `Idempotency-Key` and an authenticated OIDC `sub` representing the calling workload identity.
- New registration returns `201`; exact idempotent replay returns the original command result even if mutable lifecycle state later advances.
- Upload authorization is restricted to `application/json`, the registered object key, the package size ceiling, and a bounded one-to-fifteen-minute lifetime.
- Upload completion does not trust the client: it verifies object metadata and opens the object through digest-verifying storage before changing lifecycle state.
- `GET /api/ingestion/batches/{batchId}`, `/processing`, and `/deliveries` are read-only. The delivery query adapter reads only `ingestion_db` and never registers the Catalog command client in the API host.
- Numeric enum tokens and unsupported contract revisions are rejected; the generated string-enum contract is authoritative.
- Authentication, authorization, model-state, application, domain and persistence failures include owner, code, correlation ID, and required action.
- `/health/live` and `/health/ready` are read-only and never migrate, validate packages, repair rows, publish commands, or advance a batch.

## Persistence boundary

`ingestion_db` owns independent schemas for the active path:

- `contracts`: authorized collector producers and supported backend ingestion revisions;
- `catalog_projection`: minimal producer-owned Catalog identity/configuration projection consumed locally;
- `batches`: immutable manifest/source-policy/artifact identity plus mutable batch lifecycle and exact uploaded-object identity;
- `operations`: immutable registration and upload command results;
- `processing`: validation leases, immutable item decisions and Catalog-delivery state;
- `processing_operations`: immutable idempotent processing command results.

The app role has no `catalog_db` credentials. Business migrations run only through `Ingestion.Migrations` with `INGESTION_MIGRATOR_CONNECTION_STRING`; API and worker startup never migrate. Object storage is consumed only through `Platform.ObjectStorage.IObjectStore`; the Ingestion adapter owns the `ingestion/quarantine/` prefix and package-specific policy.

## Invariants

- Unknown contract revisions, manifest/payload/item-index digest mismatches, count mismatches, duplicate item keys, divergent object metadata, and missing objects fail closed.
- `research_only` and `forbidden` source policies cannot authorize a production item.
- `link_only` provenance may support only an external-reference field.
- An item is never silently skipped; validation persists one explicit decision for every exact item identity.
- Review may supersede only the current exact decision and cannot silently reuse a decision for changed item evidence.
- Commit selects accepted items only, creates one deterministic Catalog draft command per item, and stores one exact command result for idempotent replay.
- The same semantic idempotency scope/key with another request digest is a conflict.
- Catalog delivery is leased, bounded, retryable, and terminal outcomes are immutable; only a batch in `Committing` may lease work, and one failure does not delete other proven decisions or outcomes.
- A persisted command whose document or digest is corrupt becomes an explicit terminal delivery failure and cannot poison the claim loop indefinitely.
- Initial pending, retry-pending, leased, succeeded, and rejected rows have distinct schema-enforced shapes; recovered pre-lease-safe rows receive an explicit retry identity and failure context.
- Catalog commands create or advance drafts only. Ingestion has no publication command or publication pointer access.
- Batch identity, manifest, policies, artifacts, decisions and command-result documents reject unauthorized mutation or deletion.
- The target Site, Catalog and active Catalog configuration revision come from an Ingestion-local projection of producer-owned Catalog events. Ingestion never reads `catalog_db`.
- Registration, upload, validation, review and commit perform no cross-database transaction.

## Proof

- Domain tests cover lifecycle transitions, exact decision coverage, terminal failures, immutable supersession, partial Catalog outcomes, and stale aggregate revisions.
- Application and processing tests cover canonical package integrity, duplicate-item rejection, explicit accepted/review/rejected classification, review decision identity, idempotent commit, and draft-only Catalog command shape.
- Worker tests cover strict owner configuration and registration of the canonical validation and Catalog-delivery hosted services.
- Infrastructure and architecture tests inspect registration and processing PostgreSQL models for concurrency, semantic uniqueness, restrictive foreign keys, immutable result documents, decision supersession, one delivery per item, committing-batch claim guards, poison-command terminalization, and read/write composition isolation.
- API tests cover authentication/scope denial, workload identity, required idempotency, numeric-enum rejection, registration/upload/read behavior, delivery-ledger reads, review and commit contracts, typed missing state, and anonymous read-only liveness.
- Catalog ingestion tests prove that delivered commands remain draft-only and are revalidated by the active Catalog configuration owner.
- Architecture tests enforce context project boundaries after every project is included in the canonical solution.
- Real PostgreSQL/PostGIS, RabbitMQ, OIDC and S3-compatible acceptance remains a separate runtime proof; static or in-memory tests are not reported as that proof.
