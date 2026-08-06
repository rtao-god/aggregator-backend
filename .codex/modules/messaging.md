# Messaging building block

## Owner

`Platform.Messaging` owns only the technical at-least-once transport envelope, PostgreSQL outbox leasing, RabbitMQ publication, and delivery-attempt lifecycle. Business event names, payloads, schema identities, correlation, and causation remain producer-owned.

## Outbox table contract

Every context outbox adapter must expose the fixed `outbox_message` table inside its owner schema with the exact columns consumed by `PostgresOutboxDispatcher`: message identity, routing key, contract identity, canonical payload, payload digest, UTC occurrence, correlation/causation, lease, delivery attempt count, dispatch completion, and explicit dead-letter state. `payload_json` is exact UTF-8 text, never `jsonb`: PostgreSQL JSON normalization would change the producer bytes and invalidate their digest. A migration from `jsonb` fails closed while any row exists because the original byte representation cannot be reconstructed. Lease token, owner and expiry are either all present or all absent; dead-letter timestamp and non-empty reason are likewise an indivisible state. The table name is not consumer configuration.

The dispatcher uses `FOR UPDATE SKIP LOCKED` only for queue claiming. Before any publisher is invoked, `OutboxMessageIntegrity` verifies the canonical lowercase SHA-256 digest against the exact UTF-8 payload. The RabbitMQ adapter applies the same owner helper before opening a broker channel, so direct adapter use cannot bypass integrity. Exhausted expired leases are moved to an operator-visible dead-letter state before another claim. A failed final attempt is dead-lettered in the same failure update; it is not silently retried forever. A rejected completion or failure transition raises `OutboxLeaseLostException` with the exact message, lease and dispatcher identities. Once publication has completed, losing the lease does not attempt a second failure mutation and cannot clear or overwrite the replacement lease.

## Proof

- options validation rejects unsafe SQL identifiers and invalid delivery-attempt budgets;
- payload-integrity tests reject non-canonical digests and changed payloads before publisher or broker access;
- Catalog, Catalog Media, and Promotion migrations run in their real prerequisite order and prove exact `text` payload storage plus complete lease/dead-letter tuples on PostgreSQL;
- Catalog suppression persistence proves aggregate history and outbox atomicity, including rollback on outbox conflict;
- PostgreSQL leasing proves that a stale dispatcher cannot mutate a replacement lease;
- RabbitMQ delivery executes in CI rather than returning early when infrastructure is absent.
