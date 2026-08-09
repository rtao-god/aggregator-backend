# Catalog publication operation contract

Status: in development

## Decision

Catalog publication is a durable Catalog-owned operation. The Catalog command API accepts and persists the exact request; `catalog-worker` is the only production host that materializes and activates publications.

The previous synchronous call path:

```text
HTTP request
→ build publication bundle
→ write and verify object
→ switch pointer
→ return publication
```

is obsolete and must be removed from the active API composition root.

The canonical path is:

```text
HTTP request
→ strict contract validation
→ immutable operation snapshot
→ 202 Accepted

catalog-worker
→ atomic lease
→ exact request rehydration and digest verification
→ Catalog publication validation
→ deterministic bundle
→ object write and verification
→ Catalog transaction: publication + listing pointers + current pointer + outbox
→ operation completion
```

## Owner and source of truth

- Catalog Application owns publication validation, deterministic materialization, activation semantics, failure classification, and operation orchestration.
- Catalog Infrastructure owns PostgreSQL operation persistence, lease enforcement, atomic state transitions, and the S3-compatible adapter.
- Catalog API is transport only.
- Catalog Worker is the execution composition root.
- The operation row stores one canonical request JSON document and its digest. DTOs, SQL rows, logs, status responses, and tests are projections of that owner state.

## Public contract

`POST /api/catalog-command/catalogs/{catalogKey}/publication-requests`:

- requires Catalog publish authorization;
- requires `Idempotency-Key`;
- requires route and body catalog identity equality;
- persists actor, correlation, causation, expected current pointer, configuration revision, and exact listing selections;
- returns `202 Accepted`;
- returns a `Location` header for the operation;
- same key + same actor/scope/request digest returns the existing operation;
- same key + different digest returns conflict.

`GET /api/catalog-command/operations/{operationId}`:

- is read-only;
- returns the current typed state and exact result/failure identity;
- never claims, retries, repairs, materializes, or activates.

## Lifecycle

```text
pending
→ leased
→ retry_wait
→ leased
→ completed

pending/leased/retry_wait
→ failed
```

`completed` and `failed` are terminal. Lease expiry makes a non-terminal operation eligible for a new claim without deleting prior attempt evidence.

## Concurrency and crash behavior

- Claim uses a narrow PostgreSQL `FOR UPDATE SKIP LOCKED` query.
- Each claim creates a new lease token and increments the attempt.
- Complete/fail/retry requires the exact active lease token.
- Stale completion is rejected.
- Work is processed sequentially per operation because publication validation, object materialization, and pointer activation form one ordered owner workflow.
- Different catalogs may be processed concurrently only through independently leased operations and configured bounded worker concurrency.
- A crash before pointer activation leaves the old pointer.
- A crash after pointer activation but before operation completion is reconciled only by exact publication/operation identity; it must not create a second publication.

## Failure semantics

Terminal failures include:

- invalid immutable request;
- unsupported or stale configuration identity;
- pointer expectation conflict;
- listing concurrency conflict;
- incomplete or unapproved selection;
- provenance/media/suppression publication gate failure;
- deterministic request digest mismatch.

Retryable failures are limited to classified transient database/object-storage failures. Unknown failures are retained with owner context and do not become successful empty results.

## Persistence

Catalog migrations add:

- `catalog.publication_operation`;
- immutable request JSON and SHA-256 digest;
- idempotency scope and key;
- actor/correlation/causation;
- operation state;
- attempt, lease token/owner/expiry, next-attempt time;
- result publication identity;
- typed failure fields and timestamps;
- indexes for idempotency and eligible claim order;
- constraints for terminal/result/failure consistency.

## Proof

Required proof for this owner batch:

- API contract: `202`, `Location`, strict idempotency, read-only status.
- Application tests: same key/same digest replay, same key/different digest conflict, deterministic request snapshot.
- Worker tests: exclusive lease, stale completion rejection, bounded retry, terminal classification.
- PostgreSQL tests: constraints and claim concurrency.
- Publication tests: storage failure and DB failure leave the old pointer; successful execution activates one exact publication and completes the matching operation.
- Architecture test: Catalog API cannot call publication materialization directly; Catalog worker must reference and register Catalog Application and Infrastructure.
