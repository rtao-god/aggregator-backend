# Messaging building block

## Owner

`Platform.Messaging` owns only the technical at-least-once transport envelope, PostgreSQL outbox leasing, RabbitMQ publication, and delivery-attempt lifecycle. Business event names, payloads, schema identities, correlation, and causation remain producer-owned.

## Outbox table contract

Every context outbox adapter must expose the exact columns consumed by `PostgresOutboxDispatcher`: message identity, routing key, contract identity, canonical payload, payload digest, UTC occurrence, correlation/causation, lease, delivery attempt count, dispatch completion, and explicit dead-letter state.

The dispatcher uses `FOR UPDATE SKIP LOCKED` only for queue claiming. Exhausted expired leases are moved to an operator-visible dead-letter state before another claim. A failed final attempt is dead-lettered in the same failure update; it is not silently retried forever.

## Proof

- options validation rejects unsafe SQL identifiers and invalid delivery-attempt budgets;
- context migrations must prove the required columns and indexes on real PostgreSQL;
- broker delivery and redelivery require integration proof before release.
