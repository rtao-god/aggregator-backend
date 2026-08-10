# Analytics module

Status: in development

## Owner

Analytics is the canonical owner of accepted public interaction events, semantic idempotency, traffic-quality state, public-reference validation, listing-metrics authorization projections, durable daily aggregation runs, date-level aggregate readiness, and owner-facing daily and summary listing metrics. A click remains an interaction; it is never renamed to a lead or conversion without a separate proven source contract.

## Projects

- `Analytics.Domain`: interaction vocabulary, placement attribution, campaign allowlist, event-time bounds, traffic-quality states, and aggregate readiness semantics.
- `Analytics.Contracts`: public single/batch interaction write contracts plus daily, summary, and aggregation-status read contracts with explicit unavailable states.
- `Analytics.Application`: strict mapping of producer-owned Query activation events and Catalog listing-access events, canonical membership-digest validation, exact public-read and sponsored-placement interaction validation, anti-abuse verification port, explicit bounded batch outcomes, atomic event and access-projection registration contracts, lease-bound closed-range aggregate orchestration, deterministic listing summary ownership, typed failure translation, and fail-closed aggregate coverage/status reads.
- `Analytics.Infrastructure`: EF Core/PostgreSQL persistence for accepted events, immutable public-read/listing/placement references, monotonic activation checkpoints, exact Query and Catalog inbox results, grant-scoped listing-access projections, active/unrevoked/unexpired report authorization, lease-bound aggregate runs, immutable date results, current date-readiness pointers, complete daily aggregates, UUIDv7 identity, and read-only DB readiness.
- `Analytics.Api`: public anti-abuse proof plus single and bounded batch interaction intake; authenticated daily/summary listing metrics and protected aggregation status; strict JSON, typed access failures, resource authorization, bounded request/rate limits, and read-only health.
- `Analytics.Worker`: strict RabbitMQ consumers for Query public-read activations and Catalog listing-access changes plus bounded rebuild of closed UTC daily aggregate ranges; both consumers share one host-owned broker transport while retaining independent quorum queues and dead-letter contracts.
- `Analytics.Migrations`: one-shot owner migrations for events, access projections, messaging inbox/checkpoints, aggregates, durable aggregate runs, immutable date results, and readiness pointers. API and worker startup never apply DDL.

The obsolete parallel `AnalyticsRuntime*` contracts, service, persistence path, tests, and source generator were removed. They duplicated event and metric ownership, fabricated missing metrics as zero, and exposed a second incompatible wire contract.

## Query activation intake

```text
Query PublicReadRevision pointer switch
→ Query outbox in the same transaction
→ publisher-confirmed query.public-read-revision.activated
→ strict Analytics contract/message/digest validation
→ canonical membership-digest verification
→ serializable Analytics transaction
→ exact public-read, public-listing, and sponsored-placement rows
→ monotonic per-catalog checkpoint
→ inbox result
```

Analytics accepts the first activation only at revision `1`, then requires exact `last + 1`. Same message ID with another payload or result identity is blocking corruption. Exact replay is idempotent; a known older activation may be recorded as stale only when its immutable projection already exists. A forward gap remains retryable/unavailable and never creates partial membership. The worker uses a quorum queue with a bounded delivery limit and dead-letters contract-invalid messages; it has no `query_db` credentials and never calls Query synchronously.

## Catalog access-grant intake

```text
Catalog claim verification or grant revocation
→ Catalog grant revision + listing-access-grant.changed outbox in one transaction
→ publisher-confirmed catalog.listing-access-grant.changed
→ strict Analytics envelope, payload-digest, event-ID, permission-order, state, and time validation
→ serializable Analytics transaction
→ grant-scoped current access projection + exact message inbox
→ owner-report authorization before aggregate reads
```

The local projection is keyed by producer-owned `grant_id`, not by the lossy `(listing_id, actor_id)` pair. It retains listing, actor, granted/expiry/revocation times, exact Catalog aggregate revision, producer payload digest, and application-owned projection digest. The first observed grant revision must be `1`; the next mutation must be exact `last + 1`. Same message ID or grant revision with divergent identity or digest is blocking corruption. A known older revision is recorded as stale without regressing current authorization. A forward revision gap is retryable owner unavailability and is bounded by the quorum queue delivery limit before dead-lettering.

Report authorization succeeds when at least one local grant for the exact actor/listing is active, contains `ViewAnalytics`, has not been revoked, and has not expired at the owner clock. Expiry needs no synthetic revocation event. Revocation removes authorization immediately after its exact Catalog event commits locally. Analytics does not call Catalog, inspect claim evidence, or infer access from OIDC roles.

The worker shares the already-required RabbitMQ URI and exchanges with the Query projection consumer in the same process; queue, routing key, delivery limit, and dead-letter queue remain independently configured. A second broker URI or exchange in the access-consumer subsection cannot create another transport owner.

## Intake flow

```text
anti-abuse proof request
→ short-lived HMAC proof bound to client event ID and occurrence time
→ public interaction request
→ exact semantic key: client event ID + event kind
→ canonical payload digest
→ prior-result or digest-conflict check
→ domain and timestamp validation
→ anti-abuse verification
→ Analytics-local public-read membership validation
→ atomic event registration
→ accepted or exact already-applied response
```

The anti-abuse token is transport proof and is not persisted in the event payload digest. Repeating the same semantic event and business payload returns the original result even when the short-lived proof rotated. Reusing the semantic key with a different business payload is a blocking conflict.

The batch endpoint accepts at most 50 independent events under a bounded request body. It preflights duplicate semantic identities before side effects, then returns one exact `accepted`, `already_applied`, or typed `rejected` result per item. Only owner 4xx failures become item rejections; infrastructure/owner 5xx failures abort the batch and remain retryable at transport level. Partial acceptance is therefore explicit and replay-safe rather than a single ambiguous success flag.

`Analytics.Infrastructure` persists the event and its allowlisted campaign parameters in one transaction. PostgreSQL owns the unique semantic key `(client_event_id, event_kind)`, event-time bounds, listing/placement shape, enum ranges, and payload-digest shape. A unique-key race resolves to the exact prior event; another digest is a conflict.

## Local projection boundary

Analytics validates interactions and report authorization through local immutable projections; it never calls Query or Catalog synchronously on either path.

- `PublicReadReferenceProjection` carries one exact public-read revision, its monotonic Query activation revision, three component identities, source publication, content and membership digests, activation time, canonical sorted public listing membership after safety suppression, and exact sponsored placement references.
- Sponsored attribution validates placement ID, listing binding, scope key, and the Query-owned half-open interval `[starts_at_utc, hard_expiry_at_utc)`. An unknown, mismatched, or inactive placement is explicit invalid input, never organic fallback.
- `ListingMetricsAccessProjection` carries one exact Catalog grant identity and revision for an actor and listing, including grant/expiry/revocation time, source payload digest, projection digest, and the `ViewAnalytics` decision.
- Public-read identities are immutable. Same identity with another activation revision, component, membership, or projection digest is corruption.
- Public-read inbox/checkpoint writes and projection rows are committed atomically under a serializable transaction with owner-scoped advisory locks. Access revisions use the same fail-closed replay/gap semantics in their own owner path.

## Aggregate boundary

The worker rebuilds an explicit closed UTC range `[from, to)`, at most 31 days. Before materialization it persists one exclusive run identity, lease token, exact range, start, and lease expiry. A still-valid active lease blocks another run; an expired non-terminal run is closed as an explicit owner failure before a new run starts. Stale completion or failure cannot mutate another run.

```text
rebuilding aggregate run + exact lease
→ load exact public-read activation intervals
→ require public-reference evidence before claiming completeness
→ load exact listing memberships
→ validate every accepted event against its active revision and membership
→ count organic and sponsored impressions separately
→ compute listing, date, and run source digests
→ write complete daily rows, including observed zero
→ write immutable run item for every requested date
→ advance current date-readiness pointers
→ remove stale rows only inside the exact rebuilt range
→ complete run, metrics, run items, and readiness in one transaction
```

The run lifecycle is only `rebuilding → complete|blocked`. Run identity/range/start and completed date items are immutable; readiness pointers may advance only to a newer complete run and cannot be deleted. A complete run must cover every date in its exact range.

`GET /api/analytics/aggregation-status` reports the latest overlapping run together with exact missing dates. A current `rebuilding` or `blocked` run takes precedence over older complete date pointers. A complete run missing one of its date pointers is persistence corruption, not partial success.

A requested listing metrics range is returned only when every date has an explicit daily row. The shared range owner performs authorization before persistence read and provides the same domain rows to daily and summary transports. `complete` may contain observed zero counts. `partial`, `blocked`, and `rebuilding` contain no counts and carry an explicit unavailable reason. The summary emits numeric totals and a deterministic source digest only when every day is complete; otherwise counts/digest are absent and every unavailable day is listed explicitly.

## Persistence and migration boundary

The canonical database schemas are:

```text
events
access_projection
messaging
aggregates
```

The migration from the obsolete `analytics` schema fails closed when legacy rows exist because the old event/metric vocabulary cannot be silently converted into the canonical owner contract. The access-projection migration also fails closed when the obsolete lossy `(listing_id, actor_id)` rows exist; operators must clear the rebuildable projection and replay the complete Catalog grant stream. Runtime hosts use only the app role; the migration host owns DDL.

## Proof

- domain tests cover listing requirements, placement exposure, campaign parameter allowlisting, event-time bounds, negative metrics, and observed-zero versus unavailable states;
- application tests cover accepted intake, exact replay, digest conflict, bounded batch partial outcomes, canonical parameter ordering, unknown public-read revision rejection, lease-bound failure recording, complete/partial/rebuilding/blocked aggregate status, deterministic listing summaries, canonical Query activation ordering/digest validation, public membership ordering, and strict Catalog grant envelope/permission/state/revision validation;
- API tests cover anonymous liveness, exact anti-abuse binding, semantic and batch replay, unknown-member and numeric-enum rejection, authentication, actor mapping, observed zero, missing aggregate coverage, protected aggregation status, and complete/unavailable listing summaries;
- infrastructure model tests cover semantic-key uniqueness, event shape and time constraints, restrictive immutable public-read/listing/placement ownership, Query activation checkpoint/inbox lineage, Catalog grant-level primary identity/inbox lineage, access revision concurrency, aggregate run/date/readiness ownership, incomplete metric value shape, and absence of raw-IP storage;
- worker tests cover required shared broker configuration, prevention of divergent access-consumer transport, producer routing-key pinning, payload/message identity, retry classification, and host registration of both Analytics consumers;
- architecture tests require both complete producer-owned paths—Query.Contracts → Analytics and Catalog.Contracts → Analytics—plus grant inbox/store/authorizer reachability, batch/summary/status endpoints, lease-before-materialization ordering, atomic aggregate completion, exact Compose worker configuration, absence of Query/Catalog database credentials or synchronous clients, and structural removal of obsolete owners.

Real PostgreSQL uniqueness races, concurrent serializable projection ordering, migration execution, live RabbitMQ delivery/dead-letter behavior, and aggregate rebuild remain integration-proof requirements for the repository-wide acceptance stage.
