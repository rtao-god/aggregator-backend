# Query projection flow

## Owner boundary

Catalog owns immutable publications, publication artifacts, and the current Catalog publication pointer. Promotion owns entitlement and placement lifecycle. Query owns rebuildable public documents, facets, routes, immutable overlay components, the composite `PublicReadRevision`, and the current public-read pointer.

Query consumes producer-owned integration events and exact sealed artifacts. It never reads another context database, calls Catalog or Promotion synchronously, or selects an artifact by filename or timestamp.

## Catalog activation ordering

`PublicationSequence` identifies the immutable Catalog publication and may move backward during explicit rollback. It is not an event-ordering key.

`ActivationRevision` is the monotonically increasing Catalog-owned ordering identity for every publication/rollback activation in one catalog. Query inbox processing applies these rules:

- same event ID and payload digest is an idempotent replay;
- same event ID with another digest is corruption;
- activation revision at or below the applied checkpoint is stale and cannot change the pointer;
- a strictly newer activation revision may build and activate a new Query revision;
- gaps are allowed because a failed Catalog command may consume a revision before transaction commit.

## Base projection activation

```text
CatalogPublicationActivated
→ validate event and contract identity
→ read exact artifact key
→ verify artifact digest and embedded identities
→ materialize immutable base projection
→ create explicit empty or retained overlay components
→ validate complete document and route coverage
→ create immutable PublicReadRevision
→ atomically persist inbox, projection graph, checkpoint, and current pointer
```

The pointer switches only after every component is persisted and validated. A failed build leaves the previous public-read revision active.

## Promotion placement activation

```text
promotion.placement.changed
→ validate event identity, digest, ordering, and catalog
→ validate source revision and active base-listing membership
→ validate scope, locale, capacity slot, and hard expiry
→ materialize immutable Query-owned promotion overlay
→ create PublicReadRevision with exact existing base/safety components
→ atomically persist inbox, overlay, revision, and current pointer
```

Promotion never publishes public cards or a public overlay. Query never serves placement after `hard_expiry_at_utc`, even when a later transition event is delayed.

## Public read path

The Query API resolves one current `PublicReadRevision` at request start and uses its exact component identities through completion. Sponsored and organic rows therefore come from one snapshot. Cursor pagination is bound to both normalized query and revision; another revision is rejected instead of mixing rows.

Localization uses the exact default/supported locale policy embedded by Catalog. Query never chooses an arbitrary fallback locale. Read endpoints do not migrate, rebuild, repair, or synchronously compose another context.
