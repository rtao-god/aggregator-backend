# Query sitemap rebuild

## Preconditions

Record before mutation:

```text
owner: Query.SitemapProjection
catalog key
intended public-read revision ID
current active sitemap revision ID or explicit absence
expected immutable content digest
reason for rebuild
```

Do not use this runbook to repair data during a public request.

## Rebuild sequence

1. Load route sources from the exact Query projection inputs for the intended public-read revision.
2. Verify that no source is draft, redirecting, safety-suppressed, or a filter URL.
3. Build locale groups through `PublicSitemapDocumentBuilder`.
4. Validate all target routes and reciprocal hreflang edges.
5. Build the deterministic artifact through `BuildPublicSitemapProjectionService`.
6. Pass the exact current sitemap revision as `ExpectedCurrentPublicReadRevisionId`.
7. Persist the immutable revision and switch the pointer in one serializable transaction.
8. Read the first sitemap page and confirm its returned `PublicReadRevisionId`.
9. Follow at least one generated cursor and confirm that revision identity remains unchanged.

## Expected failures

| Code | Meaning | Required action |
|---|---|---|
| `QUERY_SITEMAP_POINTER_CONFLICT` | active pointer changed after the rebuild plan was captured | inspect the new active revision; do not overwrite it blindly |
| `QUERY_SITEMAP_REVISION_IDENTITY_CONFLICT` | the revision ID already owns different immutable content | stop and restore the correct revision identity/digest evidence |
| `QUERY_SITEMAP_HREFLANG_TARGET_MISSING` | alternate route is absent | correct the exact route group and rebuild |
| `QUERY_SITEMAP_HREFLANG_NOT_RECIPROCAL` | reverse alternate edge is absent | correct both locale records and rebuild |
| `QUERY_SITEMAP_PERSISTENCE_CONTRACT_FAILED` | PostgreSQL rejected immutable revision constraints | inspect SQLSTATE and rebuild only after correcting owner data |

## Prohibited actions

- updating or deleting immutable sitemap revision rows;
- manually changing record counts or content digests;
- selecting the newest revision by time;
- copying routes from another Catalog;
- inserting arbitrary filter URLs;
- retrying with a stale expected pointer;
- treating `503` as an empty successful sitemap.
