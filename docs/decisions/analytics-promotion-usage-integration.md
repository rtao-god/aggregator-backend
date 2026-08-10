# Analytics to Promotion closed usage integration

Status: implemented; runtime proof remains environment-dependent

## Decision

Analytics is the sole owner of interaction acceptance, traffic-quality classification, aggregate completeness, and the meaning of sponsored usage. Promotion consumes only Analytics-approved, closed-window revisions and never re-runs Analytics quality rules.

The canonical production path is:

```text
accepted sponsored interaction rows
→ exact Analytics aggregation run
→ deterministic daily placement usage derivation
→ immutable Analytics usage revision
→ Analytics transactional outbox
→ publisher-confirmed RabbitMQ delivery
→ Promotion quorum consumer
→ Promotion transactional inbox
→ immutable Promotion usage revision
→ current Promotion usage projection
```

No synchronous Analytics-to-Promotion request is part of this path. Neither bounded context has credentials for the other bounded context database.

## Producer contract

Analytics publishes:

```text
routing key:       analytics.promotion-usage-window.closed
contract identity: analytics.promotion-usage-window-closed@1
```

The event contains only the exact identities and counts required by Promotion:

- usage window identity;
- placement, listing, and Catalog identities;
- closed UTC interval;
- accepted impression, listing-open, and outbound-click counts;
- exact aggregation run identity;
- contiguous aggregate revision;
- event and occurrence identities.

Raw interactions, network data, anti-abuse evidence, user-agent detail, campaign internals, and owner authorization state remain in Analytics.

## Closed-window semantics

A usage revision is publishable only after its exact Analytics aggregation range is complete. `missing`, `incomplete`, and `complete zero` are different states:

```text
missing or incomplete evidence
→ no usage revision

complete observed zero
→ explicit zero correction revision

complete observed usage
→ explicit positive-count revision
```

An explicit zero correction is required so a later rebuild can correct an earlier non-zero result without disguising absence of evidence as zero.

## Stable stream identity and revisions

One logical stream is identified by its stable `usage_window_id`. Its placement, listing, Catalog, and UTC window identities are immutable.

```text
first materialization  → aggregate revision 1
next materialization   → current revision + 1
stale revision         → reject
revision gap           → reject and require replay/rebuild
same source digest with different counts → corruption
```

Analytics and Promotion both retain immutable revision evidence. Promotion's current projection must reference the exact immutable revision that proves it.

## Analytics transaction boundary

The Analytics aggregate transaction owns all effects that prove a complete run:

```text
daily metrics
+ date readiness
+ sponsored usage revisions
+ outbox rows
+ aggregate run completion
```

If usage derivation, revision persistence, or outbox persistence fails, the run cannot become complete. RabbitMQ availability is not part of this transaction; committed outbox rows remain retryable until publisher-confirmed delivery or explicit dead-letter state.

## Promotion transaction boundary

Promotion applies one producer message in one serializable transaction:

```text
message advisory lock
+ usage-stream advisory lock
+ exact inbox replay validation
+ contiguous revision validation
+ immutable revision insert
+ current projection switch
```

RabbitMQ acknowledgement occurs only after the transaction commits. A duplicate with the exact envelope is idempotent. Reuse of a message ID with different contract, digest, correlation, or causation is corruption.

## Transport ownership

`analytics-worker` owns the Analytics outbox dispatcher and uses the same host-owned RabbitMQ endpoint and exchange as its other broker capabilities. Outbox configuration can tune dispatcher identity, lease, attempts, batch size, and delays; it cannot silently select another broker transport.

`promotion-worker` owns a dedicated quorum queue and dead-letter route for Analytics usage events. It validates routing key, content type, encoding, contract identity, payload digest, message/event identity, correlation, causation, and strict JSON before invoking Promotion Application.

## Prohibited paths

The following are not valid alternatives:

- Promotion reading `analytics_db`;
- Analytics writing `promotion_db`;
- Promotion recalculating accepted traffic from raw interactions;
- synchronous HTTP delivery between the two owners;
- acknowledging a broker message before Promotion persistence commits;
- treating absent or incomplete evidence as zero;
- mutating an existing usage revision;
- manually advancing the current projection around a revision gap.

## Required proof

The owner contour is accepted only when the repository proves:

- deterministic derivation from independent fixtures;
- explicit zero-correction behavior;
- atomic Analytics run/outbox persistence;
- publisher-confirmed outbox delivery semantics;
- strict Promotion consumer envelope validation;
- atomic Promotion inbox/revision/current persistence;
- duplicate, stale, gap, and corruption behavior;
- absence of cross-database credentials and synchronous cross-context calls;
- successful solution build and test summaries;
- PostgreSQL migration application;
- RabbitMQ delivery, retry, and dead-letter smoke evidence.
