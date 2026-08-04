# Catalog event delivery runbook

## Runtime order

1. Run `Catalog.Migrations` as a one-shot job against the Catalog database.
2. Start `Catalog.Api` only after the migration job succeeds.
3. Start `Catalog.Worker` only after PostgreSQL and RabbitMQ are ready.

Neither the API nor the worker applies migrations. A failed migration is a deployment failure; do not start an older process against a partially changed schema.

## Required worker configuration

Environment-variable form:

```text
ConnectionStrings__Catalog=Host=...;Database=...;Username=...;Password=...
Messaging__BrokerUri=amqps://...
Messaging__Exchange=platform.events
CatalogWorker__DispatcherIdentity=catalog-worker-<stable-instance-name>
CatalogWorker__BatchSize=50
CatalogWorker__MaximumDeliveryAttempts=8
CatalogWorker__LeaseDurationSeconds=120
CatalogWorker__EmptyDelayMilliseconds=2000
```

`DispatcherIdentity` identifies an operational replica in diagnostics. Correctness does not rely on its uniqueness: every claim also carries a new UUID lease token, and completion requires that exact token.

## Delivery contract

Catalog writes the business transition and producer event envelope in one PostgreSQL transaction. The canonical JSON payload is stored as `text`; its SHA-256 identifies the exact UTF-8 bytes sent to RabbitMQ. The publisher verifies the envelope and digest before broker access, uses publisher confirms, requires routing, and rejects returned mandatory messages.

Delivery is at least once. Consumers must use an inbox keyed by producer message ID before applying a projection or side effect.

## Inspect the queue

Pending messages:

```sql
SELECT message_id,
       routing_key,
       contract_identity,
       occurred_at_utc,
       delivery_attempts,
       last_error
FROM catalog.outbox_message
WHERE dispatched_at_utc IS NULL
  AND dead_lettered_at_utc IS NULL
ORDER BY occurred_at_utc, message_id;
```

Active or expired leases:

```sql
SELECT message_id,
       lease_token,
       lease_owner,
       lease_until_utc,
       delivery_attempts
FROM catalog.outbox_message
WHERE lease_token IS NOT NULL
ORDER BY lease_until_utc, message_id;
```

Dead-letter rows:

```sql
SELECT message_id,
       routing_key,
       contract_identity,
       occurred_at_utc,
       delivery_attempts,
       dead_lettered_at_utc,
       dead_letter_reason,
       payload_digest,
       correlation_id,
       causation_id
FROM catalog.outbox_message
WHERE dead_lettered_at_utc IS NOT NULL
ORDER BY dead_lettered_at_utc, message_id;
```

## Failure interpretation

- `payload digest does not match`: persisted payload bytes were changed or produced incorrectly. Do not replay. Restore the exact producer-owned event from an authoritative backup or issue a new corrected business transition.
- `returned by exchange`: RabbitMQ topology has no route for the producer routing key. Restore the expected binding before replay.
- connection or confirmation failure: inspect RabbitMQ/PostgreSQL availability and the correlated worker diagnostic.
- `lost its exact lease`: another claim replaced the worker's lease. The stale worker must not mark the row dispatched or failed.
- exhausted attempt budget: the row is terminal and remains visible through `dead_lettered_at_utc` and `dead_letter_reason`.

## Replay policy

Replay is allowed only after the underlying defect is fixed and the following identities have been verified unchanged:

- `message_id`;
- `routing_key`;
- `contract_identity`;
- `payload_json` and `payload_digest`;
- `occurred_at_utc`;
- `correlation_id` and `causation_id`.

Never edit payload JSON, digest, event type, or contract identity in place. A semantic correction is a new producer event with a new message ID.

The current repository does not yet expose an audited replay command. Until that owner command exists, a dead-letter row must not be reset manually in production. Preserve it for diagnosis and use a new producer-owned transition when correction is required.

## Legacy migration block

`V002__catalog_durable_outbox.sql` fails when the legacy outbox contains rows. Those rows lack the digest and correlation fields required by the durable contract. Drain them through the old deployable or explicitly re-materialize them from the Catalog owner before applying the migration; do not invent missing metadata.
