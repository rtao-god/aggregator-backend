# Query public projection status

Status: implemented; runtime proof pending CI and PostgreSQL execution

## Decision

Query exposes one public-safe read-only status resource:

```text
GET /api/catalog-query/catalogs/{catalogKey}/projection-status
```

The endpoint reports only Query-owned projection evidence. It does not call Catalog, Promotion, Analytics, or Ingestion, and it cannot migrate, rebuild, replay, repair, or switch a pointer.

## Evidence model

The response is derived from one Query database read over:

```text
projection.current_public_read
projection.public_read_revision
projection.catalog_activation_checkpoint
projection.catalog_visibility_block
seo_projection.active_sitemap_revision
seo_projection.sitemap_revision
```

It exposes:

```text
current PublicReadRevision component identities
public-read activation revision and timestamp
Catalog source activation revision and checkpoint timestamp
active read-block count and oldest block timestamp
active sitemap revision, record count, build time, and activation time
```

Private suppression evidence, message payloads, dead-letter data, stack traces, and credentials remain internal.

## States

```text
ready
  current public-read pointer exists
  no active Query read block exists
  active sitemap references the exact current public-read revision

degraded
  current public-read pointer exists
  no active Query read block exists
  sitemap is missing or references another public-read revision

blocked
  one or more durable Query read blocks exist
```

Missing Query evidence is a typed `404`; it is not returned as a successful empty or unavailable projection.

## Checkpoint invariant

`catalog_activation_checkpoint.current_public_read_revision_id` records the public-read revision produced by the last applied Catalog publication activation. A later Promotion or visibility-safety overlay may create a newer composite public-read revision without changing that Catalog checkpoint.

Therefore status validation does not require equality between checkpoint revision ID and current composite revision ID. It requires that both prove the same:

```text
base_projection_id
source_publication_id
```

A different base or source publication is corruption and fails closed.

## HTTP behavior

The controller delegates to `ReadPublicProjectionStatusService`, uses the existing public rate-limit policy, and sets a short cache contract:

```text
Cache-Control: public,max-age=15,must-revalidate
```

No ETag is based only on `PublicReadRevisionId`, because a read block or sitemap pointer can change while that revision identity remains unchanged.

## Proof

Required proof consists of:

```text
application tests for ready/degraded/blocked/absent/corrupt states
API tests proving exact JSON and no listing-store access
real PostgreSQL test for pointer/checkpoint/sitemap/block reads
architecture test proving GET-only Query ownership and no foreign context
runtime-contract manifest entries
full solution build and test run
```
