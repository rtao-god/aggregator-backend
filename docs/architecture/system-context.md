# System context

## Source boundary

`aggregator-backend` is the canonical source repository for five independently deployable bounded contexts. It does not own crawling, source-specific extraction, collector evidence storage, the collector export contract, user-facing React rendering, booking, payment processing, or user reviews.

## Main flow

```text
collector-owned sealed export
→ backend-owned ingestion contract
→ package and item validation
→ Catalog immutable draft revision
→ editorial approval
→ deterministic Catalog publication
→ atomic Catalog publication pointer
→ rebuildable Query base projection
→ Promotion and safety overlays
→ atomic Query public-read pointer
→ public read-only API
```

## Runtime isolation

Each context has its own application database, SQL role, migration executable, API/worker images, outbox/inbox, and owner diagnostics. A single PostgreSQL server may host the databases locally, but no runtime credential can access another context database.

The public Query request path reads one local immutable `PublicReadRevision`; it never synchronously composes Catalog, Promotion, Analytics, or an identity-provider call to determine public domain meaning.
