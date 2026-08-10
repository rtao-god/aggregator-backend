# Analytics to Promotion usage replay runbook

## Purpose

Use this runbook when an Analytics sponsored-usage revision is committed but has not reached the Promotion usage projection, or when Promotion reports a revision gap, stale revision, duplicate corruption, or dead-lettered message.

This procedure never writes another bounded context database directly and never constructs a replacement event by hand.

## Owners

```text
Analytics aggregate/run/revision/outbox state → Analytics
RabbitMQ transport and dead-letter state      → deployment operator
Promotion inbox/revision/current state        → Promotion
```

## Initial classification

Record the exact identities before taking action:

```text
aggregation_run_id
usage_window_id
aggregate_revision
Analytics outbox message_id
payload_digest
correlation_id
causation_id
Promotion queue delivery metadata
```

Classify the failure as one of:

- Analytics run not complete;
- Analytics usage revision absent;
- Analytics outbox pending or leased;
- Analytics outbox dead-lettered;
- RabbitMQ message pending or redelivered;
- Promotion consumer contract rejection;
- Promotion revision gap;
- Promotion stale duplicate;
- same message ID with different envelope;
- Promotion inbox without immutable revision evidence.

Do not use a generic retry until this classification is known.

## Read-only Analytics inspection

Inspect the exact run and usage stream. Do not select `latest` by timestamp.

```sql
SELECT id,
       state,
       from_inclusive,
       to_exclusive,
       started_at_utc,
       completed_at_utc,
       failure_code
FROM aggregates.aggregate_run
WHERE id = :aggregation_run_id;
```

```sql
SELECT usage_window_id,
       aggregate_revision,
       placement_id,
       listing_id,
       catalog_key,
       window_starts_at_utc,
       window_ends_at_utc,
       accepted_impressions,
       accepted_listing_opens,
       accepted_outbound_clicks,
       source_digest,
       aggregation_run_id
FROM aggregates.promotion_usage_window_revision
WHERE usage_window_id = :usage_window_id
ORDER BY aggregate_revision;
```

```sql
SELECT id,
       routing_key,
       contract_identity,
       payload_digest,
       correlation_id,
       causation_id,
       attempt_count,
       lease_owner,
       lease_expires_at_utc,
       dispatched_at_utc,
       dead_lettered_at_utc,
       last_failure_code
FROM messaging.outbox_message
WHERE id = :message_id;
```

Expected invariants:

- the run is complete before an event is eligible for delivery;
- the requested revision exists and is contiguous;
- the outbox digest matches the exact stored UTF-8 payload bytes;
- terminal delivery and dead-letter fields are not partially populated.

## Read-only Promotion inspection

```sql
SELECT message_id,
       contract_identity,
       payload_digest,
       correlation_id,
       causation_id,
       received_at_utc
FROM analytics_usage_projection.inbox_message
WHERE message_id = :message_id;
```

```sql
SELECT usage_window_id,
       aggregate_revision,
       placement_id,
       listing_id,
       catalog_key,
       window_starts_at_utc,
       window_ends_at_utc,
       accepted_impressions,
       accepted_listing_opens,
       accepted_outbound_clicks,
       source_message_id,
       source_payload_digest,
       source_aggregation_run_id
FROM analytics_usage_projection.promotion_usage_window_revision
WHERE usage_window_id = :usage_window_id
ORDER BY aggregate_revision;
```

```sql
SELECT usage_window_id,
       current_aggregate_revision,
       current_revision_message_id
FROM analytics_usage_projection.promotion_usage_window
WHERE usage_window_id = :usage_window_id;
```

Expected invariants:

- an inbox row has exact immutable revision evidence;
- the current projection references an existing exact revision;
- revisions are contiguous;
- immutable event and stream identities do not change between revisions.

## Recovery actions

### Pending Analytics outbox

Allow the canonical Analytics outbox dispatcher to reclaim an expired lease and retry. Do not clear lease fields manually.

If a lease does not expire according to the configured bounded duration, treat that as an outbox implementation or clock problem and stop the worker before further diagnosis.

### Analytics outbox dead-letter

Resolve the recorded infrastructure or contract failure first. Replay the exact owner operation through the repository-provided outbox replay procedure. The replay must reuse the stored message ID, contract identity, payload bytes, digest, correlation, and causation.

Never create a new event solely to bypass the dead-letter state.

### RabbitMQ dead-letter

Compare the dead-letter payload bytes and properties with the exact Analytics outbox row. If they differ, preserve both artifacts and treat the incident as transport corruption.

After correcting topology or consumer configuration, republish the exact dead-lettered message through the approved broker replay command. Do not edit JSON in the management UI.

### Promotion revision gap

A gap means Promotion has revision `N` but received a revision greater than `N + 1`.

Replay the missing revisions in order from Analytics. Do not advance the Promotion current row and do not skip a zero-correction revision.

### Stale revision

A stale exact duplicate is safe and should remain idempotent. A stale revision with different payload identity is corruption and must not be forced through.

### Message ID reused with another envelope

Stop replay. Preserve the Analytics outbox row, broker properties, and Promotion inbox row. This is an identity-corruption incident, not a retryable duplicate.

### Promotion inbox without revision evidence

Stop the Promotion consumer. This state violates the atomic inbox/projection boundary. Restore from a verified backup or repair through an explicit owner operation that re-establishes the exact immutable evidence; do not insert a revision manually while the consumer is running.

## Verification after recovery

Verify all of the following with exact identities:

```text
Analytics run is complete
Analytics usage revision exists
Analytics outbox is dispatched exactly once logically
RabbitMQ queue no longer contains the target delivery
Promotion inbox contains the exact message envelope
Promotion immutable revision contains the exact counts and source digest
Promotion current projection points to that revision
no foreign database credentials were introduced during recovery
```

A successful replay does not require deleting duplicate delivery evidence. Duplicate transport delivery is expected; duplicate business effects are not.
