# Query module

Status: in development

## Owner

Query is the canonical owner of rebuildable public documents, facets, routes, SEO projection, immutable promotion and visibility-safety overlay components, composite `PublicReadRevision`, current public-read pointer, durable projection inboxes/blocks, the producer-owned public-read activation outbox, and the public read-only API. It does not own Catalog editorial meaning, Catalog private suppression evidence, or Promotion entitlement/schedule decisions.

## Projects

- `Query.Domain`: immutable base/overlay/public-read identities, exact suppression projections, and state contracts.
- `Query.Contracts`: public response, metadata, cursor, sponsored, organic, facet, detail, and producer-owned `PublicReadRevisionActivated` event contracts.
- `Query.Application`: exact publication build, promotion and safety materialization, deterministic public-membership event construction, revision-bound reads, cursor validation, and fail-closed projection errors.
- `Query.Infrastructure`: Query-only PostgreSQL stores, durable inbox/checkpoints/visibility blocks, atomic pointer-and-outbox switching, exact artifact reads, and safety-aware public reads.
- `Query.Api`: public read-only search/detail/facet/SEO transport; it resolves one revision snapshot per request.
- `Query.Worker`: the composition root for Catalog publication, Catalog visibility-safety, and Promotion placement consumers plus the Query outbox dispatcher.
- `Query.Migrations`: one-shot Query schema owner, including durable public-read outbox storage; runtime hosts never apply DDL.

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

```text
any successful PublicReadRevision pointer switch
→ canonical sorted public listing membership after safety suppression
→ exact sponsored placement references and hard-expiry intervals
→ deterministic membership digest
→ Query-owned PublicReadRevisionActivated payload
→ pointer state + outbox message in the same PostgreSQL transaction
→ publisher-confirmed RabbitMQ delivery
→ Analytics inbox and local public-reference projection
```

The event is minimal: it carries identities, membership, placement attribution references, digests, and activation time, never full `ListingDocument` content or private suppression evidence. Every pointer-writing path uses the same outbox writer. Publication recomposition emits only after its block is removed in the same transaction, so Analytics cannot accept a revision that Query still considers unavailable.

## Public-read invariant

At request start, Query resolves one current `PublicReadRevision` and its exact base, promotion, and safety component IDs. Search, sponsored rows, organic rows, facets, details, cache metadata, and cursors remain bound to that snapshot until response completion. No request may read an independent Promotion pointer or compose domain meaning in a frontend.

The public store refuses the catalog while any visibility block exists. Listing and route suppressions remove matching organic/sponsored documents and facet membership. Media and contact suppressions omit only the exact producer-owned child identity. Route/detail reads use the Catalog-selected public response mode: not found, gone, or temporarily unavailable. Temporary suppressions use the owner interval `[starts_at_utc, expires_at_utc)`.

Stable media and contact identities are present in Catalog publication artifact contract revision `4`, Query documents, PostgreSQL rows, and public detail contracts. Query validates an active contact target against the exact base projection before switching the safety overlay. External-reference suppression remains fail-closed at the Catalog command boundary because that stable public identity is not yet present end-to-end.

Sponsored rows preserve campaign/placement identities, slot position, disclosure label, exact overlay/public-read identities, and hard expiry. They reference existing base documents; they never copy or mutate organic ranking/content. A placement is never returned at or after `HardExpiryAtUtc`, even when expiry-event delivery is delayed.

## Sitemap projection invariant

Sitemap records are an immutable Query-owned revision bound to one exact `PublicReadRevision`. The active sitemap pointer is switched only after the complete record set, record count, canonical/self links, reciprocal hreflang groups, and digest have been validated. Public sitemap reads use revision-bound keyset cursors; they never rebuild from live search and never continue silently across another active revision.

## Projection status contract

```text
GET /api/catalog-query/catalogs/{catalogKey}/projection-status
→ current PublicReadRevision pointer and activation revision
→ exact Catalog-source checkpoint base/publication identity
→ active Query read-block count and oldest block timestamp
→ active sitemap pointer and immutable revision evidence
→ ready | degraded | blocked
```

The endpoint is read-only and public-safe. It exposes no private Catalog suppression evidence, no dead-letter payloads, and no infrastructure credentials. `ready` requires a current public-read pointer, no active Query read block, and a sitemap pointer for the exact current public-read revision. `degraded` means public reads are available while the sitemap component is missing or stale. `blocked` means Query has durable pending/failure isolation evidence and public content must not be served.

The Catalog activation checkpoint is not required to equal the current composite public-read revision after Promotion or safety overlay changes. It must instead prove the same immutable base projection and source publication. Any mismatched pointer/checkpoint/sitemap shape is owner corruption and returns a typed failure; the GET endpoint never repairs it.

## Activation ordering invariant

Catalog allocates `ActivationRevision` in the same PostgreSQL transaction as its publication pointer and outbox. Query accepts the first Catalog activation only at revision `1`, then requires every subsequent checkpoint transition to be exactly `last + 1`. Stale lower revisions may be recorded as ignored only after a later contiguous checkpoint is already proven. A forward gap cannot switch the public pointer or advance the checkpoint.

`Query.Migrations/V008__catalog_activation_revision_contiguity.sql` introduces the durable Catalog-source checkpoint invariant and rejects upgrades whose checkpoint claims revisions absent from the durable inbox. `V010__catalog_activation_upsert_contiguity.sql` preserves the same invariant for the repository's `INSERT ... ON CONFLICT DO UPDATE` checkpoint write: the trigger reads the existing owner row before validating the incoming revision. Query separately allocates a monotonic per-catalog public-read activation revision whenever the composite pointer changes; this revision is serialized into the producer event and persisted in the outbox. Recovery remains an explicit replay or rebuild operation, never a silent checkpoint reset.

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
- Worker tests cover strict inbound payload integrity, producer event identity, retry classification, bounded options, RabbitMQ redelivery, Query outbox delivery limits, and non-retryable dead-letter behavior while the event-scoped block remains active.
- Real PostgreSQL tests prove initial activation revision `1`, exact contiguous checkpoint advancement, transaction rollback of a forward gap, checkpoint UPSERT ordering, and fail-closed migration when historical inbox coverage is incomplete.
- Migration/schema tests prove exact immutable overlay reinsertion is idempotent while same-ID changed state fails with a typed PostgreSQL owner error.
- Recomposition integration proof verifies a new Catalog base preserves the exact active Promotion and safety overlays, performs one activation revision transition, updates inbox/checkpoint/pointer consistently, and removes only its own recomposition block.
- Public reads are safety-filtered for organic, sponsored, facets, routes, media, and contacts and fail with typed unavailable state while any relevant block remains.
- Projection-status application/API tests cover ready, degraded, blocked, absent, and corrupt checkpoint states; PostgreSQL proof reads exact pointer, checkpoint, block, and sitemap evidence without mutation.
- Architecture tests require every public-read pointer writer to create the producer outbox message, require a real dispatcher in `Query.Worker`, and prohibit a hidden direct Query-to-Analytics persistence path.
