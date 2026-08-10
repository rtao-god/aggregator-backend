# Messaging building block

## Owner

`Platform.Messaging` owns only the technical at-least-once transport envelope, PostgreSQL outbox leasing, RabbitMQ publication, delivery-attempt lifecycle, and the shared classification of recoverable dispatch failures. Business event names, payloads, schema identities, correlation, and causation remain producer-owned.

## Outbox table contract

Every context outbox adapter must expose the fixed `outbox_message` table inside its owner schema with the exact columns consumed by `PostgresOutboxDispatcher`: message identity, routing key, contract identity, canonical payload, payload digest, UTC occurrence, correlation/causation, lease, delivery attempt count, dispatch completion, and explicit dead-letter state. `payload_json` is exact UTF-8 text, never `jsonb`: PostgreSQL JSON normalization would change the producer bytes and invalidate their digest. A migration from `jsonb` fails closed while any row exists because the original byte representation cannot be reconstructed. Lease token, owner and expiry are either all present or all absent; dead-letter timestamp and non-empty reason are likewise an indivisible state. The table name is not consumer configuration.

The dispatcher uses `FOR UPDATE SKIP LOCKED` only for queue claiming. Before any publisher is invoked, `OutboxMessageIntegrity` verifies the canonical lowercase SHA-256 digest against the exact UTF-8 payload. The RabbitMQ adapter applies the same owner helper before opening a broker channel, so direct adapter use cannot bypass integrity. Exhausted expired leases are moved to an operator-visible dead-letter state before another claim. A failed final attempt is dead-lettered in the same failure update; it is not silently retried forever. A rejected completion or failure transition raises `OutboxLeaseLostException` with the exact message, lease and dispatcher identities. Once publication has completed, losing the lease does not attempt a second failure mutation and cannot clear or overwrite the replacement lease.

## Execution-host retry contract

`PostgresOutboxDispatcher` owns one exact dispatch attempt. When publication or integrity validation fails, it records the failed attempt, releases the lease or moves the message to dead letter when its budget is exhausted, then raises `OutboxDispatchAttemptException` with the exact message identity and terminal-state flag. The execution host owns only retry cadence and diagnostics.

`OutboxDispatchFailurePolicy` permits a host-loop retry only for:

- a typed dispatch attempt whose failure transition was persisted;
- exact lease loss, because another dispatcher now owns that transition;
- an explicitly transient Npgsql failure;
- timeout or I/O failure, including a recoverable nested/aggregate form.

An unknown mapping, schema, configuration, or programming exception is not recoverable and leaves the `BackgroundService` fail-fast. The worker cannot turn every exception into an infinite retry loop.

Catalog, Query, Analytics, and Promotion outbox hosts must:

- treat shutdown cancellation as normal termination;
- apply `OutboxDispatchFailurePolicy` before catching a dispatch exception;
- log the exact recoverable exception with the owner worker identity;
- wait the validated owner poll delay before the next claim;
- keep the process alive through transient PostgreSQL or RabbitMQ outages;
- never reset delivery attempts, lease state, or dead-letter evidence in the host loop.

A permanent payload-integrity failure is already dead-lettered by the dispatcher before the host continues. A transient broker failure remains eligible until the bounded delivery-attempt budget is exhausted. The host does not reconstruct message meaning; it applies the bounded delay only after the shared policy proves that continuing is safe.

## Proof

- options validation rejects unsafe SQL identifiers and invalid delivery-attempt budgets;
- payload-integrity tests reject non-canonical digests and changed payloads before publisher or broker access;
- retry-policy tests prove recorded attempts, lost leases, timeout/I/O, nested and aggregate failures, and unknown fail-fast behavior;
- Catalog, Catalog Media, and Promotion migrations run in their real prerequisite order and prove exact `text` payload storage plus complete lease/dead-letter tuples on PostgreSQL;
- Catalog suppression persistence proves aggregate history and outbox atomicity, including rollback on outbox conflict;
- PostgreSQL leasing proves that a stale dispatcher cannot mutate a replacement lease;
- dispatcher integration tests prove failure state is recorded before a typed exception returns to the execution host;
- architecture tests require every durable outbox host to classify, log, delay, and retry recoverable failures without a local `throw` path;
- RabbitMQ delivery executes in CI rather than returning early when infrastructure is absent.
