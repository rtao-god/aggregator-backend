# Query module

## Owner

Query is the canonical owner of rebuildable public documents, facets, routes, SEO projection, immutable promotion and visibility-safety overlay components, composite `PublicReadRevision`, current public-read pointer, durable projection inboxes/blocks, and the public read-only API. It does not own Catalog editorial meaning, Catalog private suppression evidence, or Promotion entitlement/schedule decisions.

## Projects

- `Query.Domain`: immutable base/overlay/public-read identities, exact suppression projections, and state contracts.
- `Query.Contracts`: public response, metadata, cursor, sponsored, organic, facet, and detail contracts.
- `Query.Application`: exact publication build, promotion and safety materialization, revision-bound reads, cursor validation, and fail-closed projection errors.
- `Query.Infrastructure`: Query-only PostgreSQL stores, durable inbox/checkpoints/visibility blocks, atomic pointer switching, exact artifact reads, and safety-aware public reads.
- `Query.Api`: public read-only search/detail/facet/SEO transport; it resolves one revision snapshot per request.
- `Query.Worker`: the composition root for Catalog publication, Catalog visibility-safety, and Promotion placement consumers.
- `Query.Migrations`: one-shot Query schema owner; runtime hosts never apply DDL.

## Active flows

```text
CatalogPublicationActivated
→ verify exact producer activation revision against durable checkpoint
→ per-catalog projection-mutation lease
→ durable publication-recomposition block and exact prior component capture
→ exact artifact identity and digest validation
→ immutable base projection
→ validate the captured Promotion overlay against new base membership
→ load the exact current Promotion and safety overlays
→ build one final PublicReadRevision from the new base and captured overlays
→ one atomic current pointer switch, inbox completion, checkpoint advance, and recomposition unblock
```

Candidate empty overlays emitted by the base builder are not activated when a catalog already has public state. The recomposition owner supplies the exact current immutable overlays to the persistence boundary. Re-inserting the same overlay identity with identical owner state is idempotent; reusing that identity with changed state is PostgreSQL corruption and fails closed.

```text
promotion.placement.changed
→ exact event replay/digest validation
→ validate catalog, active base membership, scope, locale, capacity, and hard expiry
→ immutable Query-owned promotion overlay
→ new PublicReadRevision preserving exact base and safety components
→ atomic current pointer switch
```

```text
catalog.public-visibility-suppression.changed
→ strict producer contract, message ID, and payload digest validation
→ durable inbox + one event-scoped catalog_visibility_block transaction
→ validate exact suppression lifecycle/revision
→ immutable visibility-safety overlay
→ new PublicReadRevision preserving exact base and promotion components
→ atomic current pointer switch + inbox completion + exact block removal transaction
```

Each safety event owns a separate durable block. Completing one event removes only its block, so another pending or failed event keeps the catalog unavailable. A failed second phase cannot expose the stale public revision.

## Public-read invariant

At request start, Query resolves one current `PublicReadRevision` and its exact base, promotion, and safety component IDs. Search, sponsored rows, organic rows, facets, details, cache metadata, and cursors remain bound to that snapshot until response completion. No request may read an independent Promotion pointer or compose domain meaning in a frontend.

The public store refuses the catalog while any visibility block exists. Listing and route suppressions remove matching organic/sponsored documents and facet membership. Media and contact suppressions omit only the exact producer-owned child identity. Route/detail reads use the Catalog-selected public response mode: not found, gone, or temporarily unavailable. Temporary suppressions use the owner interval `[starts_at_utc, expires_at_utc)`.

Stable media and contact identities are present in Catalog publication artifact contract revision `4`, Query documents, PostgreSQL rows, and public detail contracts. Query validates an active contact target against the exact base projection before switching the safety overlay. External-reference suppression remains fail-closed at the Catalog command boundary because that stable public identity is not yet present end-to-end.

Sponsored rows preserve campaign/placement identities, slot position, disclosure label, exact overlay/public-read identities, and hard expiry. They reference existing base documents; they never copy or mutate organic ranking/content. A placement is never returned at or after `HardExpiryAtUtc`, even when expiry-event delivery is delayed.

## Activation ordering invariant

Catalog allocates `ActivationRevision` in the same PostgreSQL transaction as its publication pointer and outbox. Query accepts the first Catalog activation only at revision `1`, then requires every subsequent checkpoint transition to be exactly `last + 1`. Stale lower revisions may be recorded as ignored only after a later contiguous checkpoint is already proven. A forward gap cannot switch the public pointer or advance the checkpoint.

`Query.Migrations/V008__catalog_activation_revision_contiguity.sql` introduces the durable checkpoint invariant and rejects upgrades whose checkpoint claims revisions absent from the durable inbox. `V010__catalog_activation_upsert_contiguity.sql` preserves the same invariant for the repository's `INSERT ... ON CONFLICT DO UPDATE` checkpoint write: the trigger reads the existing owner row before validating the incoming revision. Recovery remains an explicit replay or rebuild operation, never a silent checkpoint reset.

## Failure behavior

- A failed base, promotion, or safety build never exposes partial rows or switches the pointer.
- Catalog publication recomposition preserves the exact prior overlays under one mutation lease; a removed listing referenced by Promotion blocks activation rather than silently dropping the placement.
- Same event ID with another payload digest is corruption.
- Same suppression revision with another digest blocks the catalog and requires owner recovery.
- Missing Catalog activation revisions block the incoming event and preserve the prior public pointer until replay/rebuild restores the exact sequence.
- Out-of-order resolved suppression events remain pending and blocked until the exact active predecessor is applied.
- Stale source revisions, wrong catalog, invalid response mode, or unsupported child identity fail at the projection owner.
- Cursor/revision mismatch is explicit; Query never silently continues across snapshots.
- Read endpoints never migrate, rebuild, repair, or call another context synchronously.

## Proof

- Domain/application tests cover immutable components, exact suppression mapping, deterministic safety digests, publication overlay recomposition, replay/conflict, validation, and component preservation.
- Worker tests cover strict payload integrity, producer event identity, retry classification, bounded options, RabbitMQ redelivery, and non-retryable dead-letter behavior while the event-scoped block remains active.
- Real PostgreSQL tests prove initial activation revision `1`, exact contiguous checkpoint advancement, transaction rollback of a forward gap, checkpoint UPSERT ordering, and fail-closed migration when historical inbox coverage is incomplete.
- Migration/schema tests prove exact immutable overlay reinsertion is idempotent while same-ID changed state fails with a typed PostgreSQL owner error.
- Recomposition integration proof verifies a new Catalog base preserves the exact active Promotion and safety overlays, performs one activation revision transition, updates inbox/checkpoint/pointer consistently, and removes only its own recomposition block.
- Public reads are safety-filtered for organic, sponsored, facets, routes, media, and contacts and fail with typed unavailable state while any relevant block remains.
