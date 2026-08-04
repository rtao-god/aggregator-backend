# Promotion module

## Owner

Promotion is the canonical owner of product definitions, manual entitlements, listing eligibility projections, sponsored placement schedules, capacity slots, activation/expiry state, audit reasons, and the event stream consumed by Query. Promotion never owns Catalog facts, organic rank, payment processing, verification badges, or public Listing content.

## Projects

- `Promotion.Domain`: immutable product and placement revisions, entitlement lifecycle, local eligibility projection, half-open UTC schedule windows, hard expiry, scope validation, and capacity-overlap semantics.
- `Promotion.Contracts`: explicit admin API contracts plus entitlement and sponsored-placement integration events.
- `Promotion.Application`: idempotent owner commands, deterministic request and revision digests, resource validation, capacity checks, exact contract mapping, and transactional outbox effects.
- `Promotion.Infrastructure`: Promotion-only PostgreSQL persistence, local Catalog eligibility projection, atomic idempotency/outbox transactions, overlap enforcement, and readiness.
- `Promotion.Api`: authenticated product, entitlement and placement command/read endpoints with resource-scoped policies, request limits, rate limits, typed errors, and read-only health.
- `Promotion.Worker`: bounded outbox delivery and owner-scheduled entitlement/placement transitions.
- `Promotion.Migrations`: one-shot schema owner; runtime hosts never apply DDL.

## Active owner flow

```text
Catalog eligibility event
→ Promotion-local listing eligibility projection
→ create immutable Promotion product revision
→ grant manual entitlement for one listing and exact UTC window
→ create immutable sponsored placement revision for one catalog/scope/locale/slot
→ reject overlapping active or scheduled capacity
→ persist aggregate + idempotency result + outbox atomically
→ promotion.placement.changed
→ Query builds a new immutable promotion overlay
→ Query creates a new PublicReadRevision while organic rank remains unchanged
```

## Invariants

- Promotion product configuration is generic data; no Berlin or recording-studio product type exists in core code.
- Product revisions and placement revisions are immutable. Aggregate pointers advance only through optimistic-concurrency commands.
- Entitlements are listing-scoped and come only from `manual_contract`, `manual_trial`, or `administrative_grant`; payment/card/invoice fields do not exist.
- Archived, unpublished, blocking-disputed, wrong-scope, or insufficiently verified listings fail closed through the local Catalog projection.
- Promotion never changes Listing title, category, verification, source quality, editorial completeness, or organic rank.
- Every schedule is UTC and half-open. Query receives `hard_expiry_at_utc` and must not show a placement at or after that bound even if an end event is delayed.
- Capacity conflicts use exact catalog, scope, locale intersection, capacity slot and overlapping UTC window.
- Same idempotency scope/key with another request digest is a conflict.
- Placement state changes publish only producer-owned presentation metadata and identities; no Catalog document is copied into the event.
- Read endpoints do not activate, expire, repair or rebuild state.

## Proof

- Domain tests cover immutable revisions, contact prerequisites, entitlement terminal states, listing eligibility, hard expiry and capacity overlap semantics.
- Application tests cover exact command replay, outbox separation, missing eligibility fail-closed behavior and capacity conflict before persistence.
- PostgreSQL exclusion, idempotency, outbox atomicity, API authorization, worker expiry and Query overlay integration are proved by the infrastructure/API/worker/integration stages.
