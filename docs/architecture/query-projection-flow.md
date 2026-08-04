# Query projection flow

## Owner boundary

Catalog owns immutable publications, publication artifacts, and the current Catalog publication pointer. Query owns rebuildable public documents, facets, routes, overlays, the composite `PublicReadRevision`, and the current public-read pointer.

Query consumes only the producer-owned `CatalogPublicationActivated` event and the exact sealed artifact referenced by that event. Query does not read `catalog_db`, call Catalog synchronously, or select an artifact by filename or timestamp.

## Activation ordering

`PublicationSequence` identifies the immutable Catalog publication and may move backward during an explicit rollback. It is therefore not an event-ordering key.

`ActivationRevision` is the monotonically increasing, Catalog-owned ordering identity for every publication or rollback activation in one catalog. Query inbox processing compares this revision with its local catalog checkpoint:

- the same event ID and payload digest is an idempotent replay;
- the same event ID with another digest is corruption;
- an activation revision at or below the applied checkpoint is stale and cannot change the pointer;
- a strictly newer activation revision may build and activate a new Query revision;
- gaps are allowed because a failed Catalog command may consume an activation revision before its transaction commits.

## Projection activation

```text
CatalogPublicationActivated
→ validate event and contract identity
→ read exact artifact key
→ verify artifact digest
→ validate event/artifact identities
→ materialize immutable base projection
→ materialize explicit promotion overlay revision
→ materialize explicit visibility-safety overlay revision
→ validate complete document and route coverage
→ create immutable PublicReadRevision
→ atomically persist inbox, projection graph, checkpoint, and current pointer
```

The current pointer is switched only after every component is persisted and validated. A failed build leaves the previous public-read revision active.

## Public read path

The Query API resolves one current `PublicReadRevision` at the beginning of a request and uses its exact component identities throughout that request. Cursor pagination is bound to both the normalized query and that revision. A cursor from another revision is rejected explicitly instead of continuing against mixed rows.

Localization uses the exact default and supported locale policy embedded by Catalog in the publication artifact. Query never chooses an arbitrary fallback locale.
