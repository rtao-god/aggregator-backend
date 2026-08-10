# Promotion module

Status: in development

## Owner

Promotion is the canonical owner of promotion products, manual entitlement, sponsored-placement schedules, capacity conflicts, activation windows, audit reasons, paid-placement lifecycle, and the local ordered projection of Analytics-approved placement usage.

It does not own Catalog facts, verification badges, organic ranking, payment processing, tax, Analytics traffic-quality classification, or Query presentation.

## Projects

- `Promotion.Domain`: products, immutable product revisions, entitlements, placement windows/revisions, scope eligibility, and capacity invariants.
- `Promotion.Contracts`: admin API and producer-owned `PromotionPlacementChanged` event contract.
- `Promotion.Application`: commands, idempotency, optimistic concurrency, explicit mapping, event creation, deterministic canonical JSON, scheduled activation policy, strict application of Analytics-owned usage revisions without reclassification, and typed owner failures.
- `Promotion.Infrastructure`: Promotion-only EF/PostgreSQL persistence, listing-eligibility projection, capacity rows, command results, durable outbox, eligibility reconciliation, atomic schedule transition units, and a serializable Analytics usage inbox/current/revision store.
- `Promotion.Api`: authenticated product/entitlement/placement commands and reads; thin controllers only.
- `Promotion.Worker`: scheduled lifecycle transitions, Catalog eligibility consumption, Analytics usage consumption, automatic fail-closed placement pause, and outbox dispatch; no HTTP surface and no migrations.
- `Promotion.Migrations`: the only owner of Promotion database schemas, including Catalog eligibility and Analytics usage projections.

## Active flow

```text
Promotion product command
→ immutable product revision
→ current product pointer

manual entitlement command
→ listing-scoped entitlement
→ entitlement outbox

placement command
→ Catalog-derived eligibility projection check
→ product/entitlement/time-window validation
→ capacity conflict check
→ immutable placement revision
→ current placement pointer + capacity rows
→ placement outbox
→ Query sponsored overlay

Catalog listing publication/archive eligibility event
→ Promotion inbox + monotonic local eligibility projection
→ replay-safe placement reconciliation
→ automatic pause of invalid scheduled/active placements
→ capacity exclusion removal + correlated placement outbox
→ Query sponsored overlay

scheduled transition scan
→ select bounded due candidate identities
→ one entitlement transition per serializable transaction
→ acquire the Catalog eligibility listing-stream lock before each placement transaction snapshot
→ lock current placement, entitlement, and product rows
→ re-read current local eligibility
→ activate, end, or fail-closed pause exactly one placement
→ capacity rows + placement outbox in the same transaction

Analytics complete sponsored-usage revision
→ Promotion quorum consumer
→ strict Promotion envelope/message/digest validation
→ serializable inbox + immutable usage revision
→ contiguous current usage projection
→ RabbitMQ acknowledgement after commit
```

Eligibility recovery never resumes a placement. Resume remains an explicit Promotion command that rechecks the current local eligibility projection.

All public IDs use UUID identities. Aggregate revision is distinct from immutable revision identity and event identity.

## Product model

A Promotion Product owns:

- stable product key;
- active/inactive/archived lifecycle;
- immutable localized display names;
- presentation features;
- verified-contact requirement;
- optional exact contact capability requirement;
- aggregate revision.

Changing product terms creates a new immutable product revision. Product update never changes placement presentation directly; Query receives only placement event output.

## Entitlement model

Entitlement source is explicitly one of:

- manual contract;
- manual trial;
- administrative grant.

Entitlement is listing-scoped, has an exact product key and UTC half-open time window, and moves only through explicit scheduled/active/paused/revoked/expired states. Payment, invoices, refunds, tax, and recurring billing are not represented.

## Placement model

A Sponsored Placement owns:

- exact entitlement and listing IDs;
- immutable placement revision;
- Catalog/category/district/editorial scope;
- locale scope;
- UTC half-open window;
- priority band;
- capacity slot;
- presentation-label key;
- current state;
- audit actor/reason/time;
- aggregate revision.

Create, revise, resume, and due-time activation require the exact current entitlement, product, and listing eligibility projection. Paused, ended, revoked, expired, archived, disputed, or unpublished state fails closed.

Capacity exclusion is represented by transactionally maintained `sponsored_placement_capacity` rows and a PostgreSQL exclusion constraint over exact catalog/scope/locale/slot time ranges. Capacity is not claimed while a placement is paused, ended, or revoked. An in-memory precheck is advisory only; the database is authoritative.

The scheduler does not wrap an arbitrary batch in one transaction. Each entitlement or placement is an independent atomic unit, so a later invalid row cannot roll back already committed transitions. Placement activation uses the same per-listing advisory lock as Catalog eligibility projection/reconciliation and acquires it before opening the serializable transaction, preventing an older transaction snapshot from activating against a newly committed ineligible revision.

## Analytics usage boundary

Analytics publishes one producer-owned closed usage revision for an exact placement and UTC day. Promotion validates the RabbitMQ routing key, contract identity, UTF-8 JSON bytes, payload SHA-256, message/event identity, correlation/causation, closed window, non-negative counts, and positive aggregate revision before entering the Application boundary. It does not inspect raw interactions or recalculate Analytics quality meaning.

```text
Analytics usage event
→ worker wire validation
→ ApplyAnalyticsPromotionUsageWindowService
→ message and placement-window advisory locks
→ exact inbox replay/corruption check
→ contiguous aggregate revision check
→ immutable revision insert
→ current projection insert/update
→ transaction commit
→ ACK
```

The first stream revision must be `1`; the next must be exactly `current + 1`. A forward gap is retryable owner unavailability so a delayed preceding revision may still arrive within the bounded quorum-queue delivery budget. A stale revision is a permanent conflict and cannot regress current state. Same message ID with a different envelope is corruption. Placement, listing, Catalog, and UTC-window identity cannot change across revisions. A complete zero-valued correction is valid owner output; an absent Analytics revision remains missing and is not materialized as zero.

The consumer uses its own quorum queue and dead-letter queue while sharing the Promotion worker's canonical RabbitMQ URI and event exchange. The Promotion API does not register the usage application/store path and cannot consume or repair usage. Promotion stores only Analytics-owned aggregate results in `promotion_db`; it has no `analytics_db` credential or synchronous Analytics client.

## Query boundary

Promotion publishes minimal placement projection events containing only:

- placement and listing identities;
- Catalog/scope/locale identities;
- active window and hard expiry;
- priority/capacity/presentation metadata;
- state and aggregate revision;
- changed-at time.

It never sends listing title, contact facts, verification evidence, prices, or organic rank. Query retains immutable received event history, materializes an immutable Promotion Overlay, and atomically switches the Promotion Overlay pointer together with the composite Public Read Revision. It does not synchronously call Promotion.

## HTTP boundary

`Promotion.Api` requires external bearer authentication and operation-specific scopes:

- `promotion.manage-products`;
- `promotion.manage-entitlements`;
- `promotion.manage-placements`;
- `promotion.read`.

Write commands require one stable `Idempotency-Key`. Exact replay returns the original response with `Idempotent-Replay: true`; the same key with another canonical request digest returns conflict. Route/body identity mismatch, invalid JSON, numeric enum tokens, actor mapping failure, and owner failures use the shared RFC 7807 envelope.

## Persistence and messaging boundary

Promotion persists only in `promotion_db`:

- product and immutable revisions;
- entitlement state;
- placement and immutable revisions;
- current capacity rows;
- listing promotion eligibility projection with exact published listing revision and source-event lineage;
- automatic eligibility reconciliation state changes and placement outbox effects;
- idempotent command results;
- exact-text correlated outbox rows;
- Analytics usage inbox rows, immutable usage revisions, and one current revision pointer per stable placement/day stream.

Business state and event envelope are committed in one serializable Promotion transaction. Outbox rows retain routing key, contract identity, exact payload text and digest, correlation/causation, lease, delivery attempts, dispatch completion, and indivisible dead-letter state. Analytics usage inbox, immutable revision, and current projection are likewise one serializable transaction; acknowledgement occurs only after commit. Scheduled state changes, usage consumption, and outbox dispatch use Promotion-owned stores; no API or worker applies migrations.

## Proof

- Promotion domain tests cover invalid product revision input, entitlement overlap, scope mismatch, window rules, lifecycle transitions, and capacity overlap semantics;
- Promotion eligibility tests prove archived/disputed/unpublished listings fail closed, product contact requirements remain enforced, and recovery does not auto-resume placements;
- Promotion consumer tests prove inbox replay still invokes reconciliation after a crash boundary and preserves Catalog message causation;
- Promotion scheduling tests prove missing eligibility, ineligible Catalog state, ineffective entitlement, and paused state cannot produce automatic activation;
- Catalog eligibility architecture tests prove the consumer writes/replays the projection before reconciliation, acknowledges only after reconciliation, removes capacity rows, emits placement events, and depends only on `Catalog.Contracts`;
- scheduling architecture tests require independent transaction units, pre-snapshot listing-stream locking, row locks, local eligibility reads, and no automatic resume;
- Promotion application tests cover product/entitlement/placement command orchestration, idempotent replay, stale revision rejection, exact correlated events, strict Analytics usage mapping, and observed-zero correction acceptance;
- Promotion infrastructure tests cover EF uniqueness, exclusion constraints, immutable-history guards, exact-text outbox, command-result shape, shared PostgreSQL data-source ownership, Analytics usage inbox/revision/current constraints, and stale/gap/corruption semantics;
- Promotion API tests cover authentication, authorization, strict enum wire behavior, route/body mismatch, duplicate replay, and typed failures;
- Promotion worker tests cover fail-fast transport configuration, producer-owned Analytics routing-key pinning, strict message/digest identity, retry classification, quorum/dead-letter topology, and ACK-after-commit ordering;
- Query application tests cover duplicate/stale/gap Promotion event handling and deterministic overlay rebuild;
- Query infrastructure tests cover immutable event/projection enforcement and overlay-pointer switching;
- cross-context architecture tests require the complete Analytics.Contracts → Promotion worker → Application → serializable store → Promotion database path, the complete Analytics usage revision/outbox producer path, exact Compose runtime configuration, and the absence of foreign database credentials or synchronous clients;
- architecture tests block Promotion state in Catalog/Query/Analytics, cross-database references, missing Promotion contract dependency, and legacy payment/billing concepts.
