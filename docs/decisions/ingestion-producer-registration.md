# Ingestion producer-registration owner

## Decision

`producer_registration` is an Ingestion-owned authorization registry. It is not a migration seed, a startup bootstrap table, or operator-edited configuration.

A package producer is accepted only when the exact authenticated OIDC workload subject has a current active registration containing the exact candidate-ingestion contract revision used by the request.

## Owner flow

```text
privileged service identity
+ ingestion.manage-producers
+ Idempotency-Key
+ exact expected aggregate revision
→ PUT /api/ingestion/producer-registrations
→ immutable producer_registration_revision
→ current producer_registration pointer
→ immutable producer_registration_command result
```

`GET /api/ingestion/producer-registrations?producerIdentity=...` reads the current selected revision. Deactivation creates a new revision with `active = false`; hard delete and in-place revision mutation are prohibited.

## Concurrency and replay

The PostgreSQL adapter uses one serializable transaction with separate advisory locks for:

- command scope and idempotency key;
- producer aggregate identity.

A replay is accepted only when the same command identity carries the same request digest. A reused idempotency key with a different request is a conflict. The result ledger stores canonical result bytes and their exact SHA-256 digest.

Producer changes require optimistic `ExpectedAggregateRevision`:

- `0` creates revision `1` only when the producer is absent;
- an existing producer requires its exact current aggregate revision;
- every successful mutation creates exactly the next revision.

## Persistence

Migration `V008__producer_registration_owner.sql`:

- fails closed when legacy manually inserted registrations exist;
- adds current aggregate revision and content identity;
- creates immutable `producer_registration_revision` history;
- creates immutable `producer_registration_command` replay history;
- binds the current row to its exact history revision with a deferrable composite foreign key.

The migration intentionally does not fabricate lineage for existing rows. An installation with manual legacy registrations must remove them and recreate registrations through the owner command.

## Consumer boundary

Batch registration consumes producer authorization only through:

```text
IIngestionProducerRegistry
→ IngestionProducerRegistry
→ IIngestionProducerRegistrationStore
→ PostgresIngestionProducerRegistrationStore
```

The former EF producer row/reader is removed. API controllers, acceptance code, migrations, and deployment scripts must not insert producer registrations directly.

## Proof

Required proof is maintained by:

- application tests for revision, contract-revision, UTC, reason, and digest invariants;
- API tests for privileged scope, exact caller identity, read, replay, and idempotency conflict;
- persistence tests for legacy rejection, immutable history, exact-byte result digest, and serializable locks;
- architecture tests preventing a second EF model, manual SQL writer, or direct authorization formula.
