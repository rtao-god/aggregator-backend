# Query sitemap read model

Status: in development

## Decision

Query owns a dedicated immutable sitemap read model. A public sitemap request never runs full search, does not reconstruct SEO meaning from current Catalog state, and cannot repair missing projection data.

```text
Query-approved route sources
→ Query SEO domain validation
→ reciprocal locale route groups
→ deterministic sitemap artifact digest
→ immutable sitemap revision
→ optimistic active pointer switch
→ revision-bound paginated reads
```

## Ownership

- `Query.Domain` owns indexability, self-canonical, route-path, locale, and per-document hreflang invariants.
- `Query.Application` owns source grouping, cross-document reciprocity, deterministic digesting, cursor scope, and explicit unavailable read state.
- `Query.Infrastructure` owns serializable activation, immutable PostgreSQL revisions, optimistic pointer concurrency, and repeatable-read keyset pagination.
- `Query.Api` exposes only read-only sitemap records.
- Catalog, Promotion, Analytics, and frontend code do not compute sitemap meaning.

## Physical model

The Query database contains:

```text
seo_projection.sitemap_revision
seo_projection.sitemap_record
seo_projection.sitemap_hreflang
seo_projection.active_sitemap_revision
```

Revision manifests, records, and hreflang rows are immutable. The active pointer is the only mutable sitemap state and references one exact `(catalog_key, public_read_revision_id)` revision.

Activation verifies the immutable manifest record count before commit. Deferred constraints require:

- each sitemap record to have an exact self hreflang link;
- every alternate route to exist in the same revision;
- every hreflang edge to have its exact reverse edge;
- canonical path to equal the route path;
- filter/query/fragment, draft, redirect, and suppressed routes to remain non-indexable.

## Concurrency and replay

Activation uses one serializable transaction and one Catalog-scoped PostgreSQL advisory lock.

```text
same revision + same digest + already active
→ Duplicate

same revision + different digest/record count/build time
→ identity conflict

current pointer != expected pointer
→ concurrency conflict

valid new or existing immutable revision + exact expected pointer
→ atomic active pointer switch
```

A stale build cannot silently reactivate an old sitemap revision. Content digest excludes the expected pointer and remains a deterministic function of exact revision content.

## Read contract

```http
GET /api/catalog-query/catalogs/{catalogKey}/sitemap-records
```

Inputs:

```text
locale optional
pageSize 1..1000
cursor optional
```

The cursor binds:

```text
publicReadRevisionId
catalogKey
requested locale
last locale
last path
```

Changing Catalog, locale, or active revision invalidates continuation. The store reads pointer, records, and hreflang rows in one repeatable-read snapshot and uses `(locale, path)` keyset pagination.

No active sitemap projection returns explicit `503 QUERY_SITEMAP_PROJECTION_UNAVAILABLE`; it is never represented as a successful empty sitemap.

## Recovery

A public GET cannot build, retry, or repair the sitemap. Recovery is an explicit Query projection operation:

1. identify the exact intended public-read revision;
2. rebuild route sources from the exact Query projection inputs;
3. verify reciprocal hreflang and deterministic content digest;
4. provide the exact expected current sitemap revision;
5. activate the rebuilt immutable revision;
6. retry the read with a fresh cursor.

Never select a revision by filename, timestamp, or “latest” discovery.
