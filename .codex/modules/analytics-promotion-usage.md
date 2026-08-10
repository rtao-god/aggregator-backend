# Analytics to Promotion usage module

Status: in development until environment-level broker and migration proof is recorded

## Owners

- Analytics owns interaction acceptance, anti-abuse/quality classification, aggregation completeness, deterministic sponsored-usage derivation, immutable usage revisions, and the producer outbox.
- Promotion owns the consumer inbox, contiguous local usage revision history, and the current Promotion usage projection.
- RabbitMQ is transport only. It does not own event meaning, replay identity, or revision progression.

## Projects

### Analytics

- `Analytics.Contracts`: `PromotionUsageWindowClosed` and its routing/contract identity.
- `Analytics.Application`: closed-window validation, deterministic derivation, canonical event/outbox construction.
- `Analytics.Infrastructure`: aggregate-run materialization, immutable usage revisions, transactional outbox persistence.
- `Analytics.Worker`: aggregate execution and publisher-confirmed outbox dispatch.
- `Analytics.Migrations`: aggregate usage stream and durable outbox schemas.

### Promotion

- `Promotion.Application`: strict consumer message mapping and Promotion-local usage projection owner.
- `Promotion.Infrastructure`: serializable inbox/revision/current persistence.
- `Promotion.Worker`: quorum consumer, strict envelope validation, retry/dead-letter behavior, ACK-after-commit.
- `Promotion.Migrations`: inbox, immutable revision history, current projection, and database constraints.

## Canonical path

```text
Analytics accepted sponsored interactions
→ complete aggregate run
→ deterministic placement/day usage
→ immutable Analytics revision
→ Analytics outbox in the same transaction
→ RabbitMQ publisher confirm
→ Promotion strict consumer
→ Promotion inbox and immutable revision in one transaction
→ Promotion current projection
```

## Invariants

- No synchronous cross-context HTTP call exists in the contour.
- Analytics cannot write `promotion_db`; Promotion cannot read `analytics_db`.
- Only accepted Analytics interactions contribute to usage.
- Missing/incomplete evidence does not become zero.
- Complete observed zero is an explicit correction revision.
- Stream identity is stable; revisions are contiguous.
- Same message ID with a different envelope is corruption.
- Same source digest with different counts is corruption.
- Promotion does not repeat Analytics quality rules.
- RabbitMQ ACK is unreachable before Promotion persistence commits.
- Read paths never repair gaps or mutate usage state.

## Failure and replay

- Analytics transaction failure leaves the aggregate run incomplete and produces no deliverable outbox effect.
- Broker unavailability after commit leaves the exact Analytics outbox row pending.
- Promotion transient persistence failure causes bounded redelivery.
- Permanent contract failures and revision gaps are dead-lettered with exact evidence.
- Replay uses the original message ID, payload bytes, digest, correlation, causation, usage-window identity, and aggregate revision.
- A missing revision is replayed before any later revision; the current Projection is never manually advanced around a gap.

## Proof map

- Analytics Application tests: derivation, identity stability, zero corrections, canonical payload and digest.
- Analytics Infrastructure tests: aggregate-run atomicity, revision and outbox persistence.
- Promotion Application tests: strict event validation without traffic-quality duplication.
- Promotion Infrastructure tests: inbox replay, immutable revisions, stale/gap/corruption handling.
- Promotion Worker tests: topology, envelope integrity, retry classification, ACK ordering.
- Architecture tests: composition-root reachability, producer-owned contracts, no cross-database credentials, no synchronous cross-context calls.
- Environment proof: migrations, build/test summaries, RabbitMQ delivery/redelivery/DLQ, Compose smoke.

Operational recovery follows `docs/runbooks/analytics-promotion-usage-replay.md`. The full decision is recorded in `docs/decisions/analytics-promotion-usage-integration.md`.
