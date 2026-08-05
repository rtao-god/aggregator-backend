# Promotion module

## Owner

Promotion is the canonical owner of product definitions, manual entitlements, listing eligibility projections, sponsored placement revisions and schedules, capacity slots, activation/expiry state, audit reasons, and producer events consumed by Query. Promotion never owns Catalog facts, organic rank, payment processing, verification badges, public Listing content, or a materialized public overlay.

## Projects

- `Promotion.Domain`: immutable product and sponsored-placement revisions, entitlement lifecycle, local eligibility projection, half-open UTC windows, hard expiry, scope validation, and capacity-overlap semantics.
- `Promotion.Contracts`: explicit admin API contracts plus entitlement and sponsored-placement integration events.
- `Promotion.Application`: one canonical `PromotionCommandIdentity`, idempotent owner commands, deterministic request/revision digests, eligibility checks, capacity checks, exact contract mapping, and transactional outbox effects.
- `Promotion.Infrastructure`: Promotion-only PostgreSQL persistence, local Catalog eligibility projection, atomic idempotency/outbox transactions, overlap enforcement, scheduling, and readiness.
- `Promotion.Api`: authenticated product, entitlement, placement and calendar command/read endpoints with scoped policies, request limits, rate limits, typed errors, and read-only health.
- `Promotion.Worker`: bounded schedule transitions plus dispatch of committed outbox messages; it never migrates, changes Catalog content, or builds a public read model.
- `Promotion.Migrations`: one-shot schema owner; runtime hosts never apply DDL.

There is no separate campaign aggregate or Promotion-owned public-card/public-overlay publication. Product, entitlement, and placement already own schedule, capacity, idempotency, and sponsored eligibility. Query alone materializes their public projection.

## API boundary

- Audience: `aggregator-promotion`.
- Listing management scope: `promotion.manage-listing`.
- Catalog placement/product scope: `promotion.manage-catalog`.
- Read scope: `promotion.read`.
- Development contract-document scope: `promotion.test-contracts`.
- Every mutating command requires one exact `Idempotency-Key` and an authenticated internal `actor_id` mapping.
- Product, entitlement, placement and placement-calendar reads remain read-only.
- Numeric enum tokens and undeclared JSON members are rejected by the active wire contract.
- `/health/live` and `/health/ready` are anonymous read-only probes; they never migrate, activate, expire or repair Promotion state.

## Active owner flow

```text
Catalog eligibility event
→ Promotion-local listing eligibility projection
→ immutable Promotion product revision
→ listing-scoped manual entitlement with exact UTC window
→ immutable sponsored placement revision for catalog/scope/locale/slot
→ overlap/capacity validation
→ aggregate + idempotency result + outbox in one transaction
→ bounded worker activation/expiry transition
→ promotion.placement.changed
→ Query materializes an immutable overlay component
→ Query creates and atomically selects a new PublicReadRevision
```

## Invariants

- Promotion product configuration is generic data; no vertical-specific product type exists in core code.
- Product and placement revisions are immutable. Aggregate pointers advance only through optimistic-concurrency commands.
- Entitlements are listing-scoped and come only from `manual_contract`, `manual_trial`, or `administrative_grant`; payment/card/invoice fields do not exist.
- Archived, unpublished, blocking-disputed, wrong-scope, or insufficiently verified listings fail closed through the local Catalog projection.
- Promotion never changes Listing title, category, verification, source quality, editorial completeness, or organic rank.
- Every schedule is UTC and half-open. Query receives `hard_expiry_at_utc` and must not show a placement at or after that bound even if an end event is delayed.
- Capacity conflicts use exact catalog, scope, locale intersection, capacity slot and overlapping UTC window.
- Same idempotency scope/key with another request digest is a conflict.
- Placement events contain only producer-owned placement/presentation identities; no Catalog document is copied into the event.
- Read endpoints do not activate, expire, repair or rebuild state.

## Proof

- Domain tests cover immutable revisions, contact prerequisites, entitlement terminal states, listing eligibility, hard expiry, and capacity overlap semantics.
- Application tests cover exact command replay, outbox separation, missing eligibility fail-closed behavior, and capacity conflict before persistence.
- Infrastructure tests inspect Promotion PostgreSQL ownership, optimistic concurrency, immutable revision rows, command-result identity, and overlap constraints.
- API tests cover anonymous denial, missing actor mapping, required idempotency, numeric-enum rejection, undeclared-field rejection, exact create/replay identity, authorized read, and anonymous liveness.
- Worker tests cover strict bounded options and schedule/outbox composition.
- Real PostgreSQL/RabbitMQ delivery and Query materialization remain integration/E2E proof and are not inferred from in-memory API tests.
