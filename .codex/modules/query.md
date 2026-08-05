# Query module

## Owner

Query is the canonical owner of rebuildable public documents, facets, routes, SEO projection, immutable promotion and visibility-safety overlay components, composite `PublicReadRevision`, current public-read pointer, and the public read-only API. It does not own Catalog editorial meaning or Promotion entitlement/schedule decisions.

## Projects

- `Query.Domain`: immutable base/overlay/public-read identities and state contracts.
- `Query.Contracts`: public response, metadata, cursor, sponsored, organic, facet, and detail contracts.
- `Query.Application`: exact publication build, promotion materialization, revision-bound reads, cursor validation, and fail-closed projection errors.
- `Query.Infrastructure`: Query-only PostgreSQL stores, inbox/checkpoints, atomic pointer switching, exact artifact reads, and readiness.
- `Query.Api`: public read-only search/detail/facet/SEO transport; it resolves one revision snapshot per request.
- `Query.Worker`: the single composition root for Catalog publication and Promotion placement consumers.
- `Query.Migrations`: one-shot Query schema owner; runtime hosts never apply DDL.

## Active flows

```text
CatalogPublicationActivated
→ exact artifact identity and digest validation
→ immutable base projection
→ explicit overlay components
→ immutable PublicReadRevision
→ atomic current pointer switch
```

```text
promotion.placement.changed
→ exact event replay/digest validation
→ validate catalog, active base membership, scope, locale, capacity, and hard expiry
→ immutable Query-owned promotion overlay
→ new PublicReadRevision preserving exact base and safety components
→ atomic current pointer switch
```

## Public-read invariant

At request start, Query resolves one current `PublicReadRevision` and its exact base, promotion, and safety component IDs. Search, sponsored rows, organic rows, facets, details, cache metadata, and cursors remain bound to that snapshot until response completion. No request may read an independent Promotion pointer or compose domain meaning in a frontend.

Sponsored rows preserve campaign/placement identities, slot position, disclosure label, exact overlay/public-read identities, and hard expiry. They reference existing base documents; they never copy or mutate organic ranking/content. A placement is never returned at or after `HardExpiryAtUtc`, even when expiry-event delivery is delayed.

## Failure behavior

- A failed base or promotion build never exposes partial rows or switches the pointer.
- Same event ID with another payload digest is corruption.
- Stale source revisions, wrong catalog, missing base listing, invalid scope/locale/capacity, or expired placement fail at the projection owner.
- Cursor/revision mismatch is explicit; Query never silently continues across snapshots.
- Read endpoints never migrate, rebuild, repair, or call another context synchronously.

## Proof

- Domain/application tests cover immutable components, composite revision creation, replay/conflict, validation, and exact component preservation.
- Infrastructure tests cover transactionality, inbox/checkpoint behavior, immutable rows, and atomic pointer replacement.
- API tests cover sponsored/organic response composition, revision metadata, ETag/cache bounds, cursor binding, and explicit missing/unavailable states.
- Real PostgreSQL, RabbitMQ, object-storage, migration, and production-path E2E proof remain mandatory before release completion.
