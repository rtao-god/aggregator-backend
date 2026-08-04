# Analytics module

## Owner

Analytics is the canonical owner of accepted public interaction events, their semantic idempotency, traffic-quality state, aggregate readiness, and owner-facing listing metrics. A click remains an interaction; it is never renamed to a lead or conversion without a separate proven source contract.

## Projects

- `Analytics.Domain`: interaction vocabulary, public placement attribution, campaign allowlist, event-time bounds, traffic-quality states, and aggregate readiness semantics.
- `Analytics.Contracts`: public event write contracts and owner metrics read contracts.
- `Analytics.Application`: exact public-read membership validation, anti-abuse verification port, atomic event registration contract, typed failure translation, local projection write contracts, and fail-closed aggregate coverage reads.
- `Analytics.Infrastructure`: EF Core/PostgreSQL persistence for accepted events, immutable public-read membership, listing access revisions, and daily aggregate readiness; UUIDv7 identity and read-only DB readiness adapters.

API, worker, and migration projects are not yet active Analytics owners. They must consume these contracts without adding raw-IP domain storage, synchronous Query/Catalog calls, or incomplete-as-zero fallback behavior.

## Intake flow

```text
public interaction request
→ exact semantic key: client event ID + event kind
→ canonical payload digest
→ prior-result or digest-conflict check
→ domain and timestamp validation
→ anti-abuse proof
→ Analytics-local public-read membership validation
→ atomic event registration
→ accepted or exact already-applied response
```

The anti-abuse token is transport proof and is not persisted in the event payload digest. Repeating the same semantic event and business payload can return the original result even when the short-lived proof rotated. Reusing the semantic key with a different business payload is a blocking conflict.

`Analytics.Infrastructure` persists the event and its allowlisted campaign parameters in one EF transaction. PostgreSQL owns the unique semantic key `(client_event_id, event_kind)`, event-time bounds, listing/placement shape, enum ranges, and payload-digest shape. A unique-key race resolves to the exact prior event; another digest is returned as corruption conflict.

## Local projection boundary

Analytics validates interactions and report authorization through local immutable projections; it never calls Query or Catalog synchronously on either path.

- `PublicReadReferenceProjection` carries one exact public-read revision, its three component identities, source publication, content and membership digests, activation time, and canonical sorted public listing membership after safety suppression.
- `ListingMetricsAccessProjection` carries one exact Catalog access revision for an actor and listing, including the source payload digest and `view_listing_analytics` decision.
- Empty or duplicate listing identities, unknown placement exposure values, non-UTC times, malformed digests, and non-positive access revisions fail at the Application/Domain owner boundary.
- Public-read identities are immutable. Same identity with another component or membership digest is corruption.
- Access revisions are applied under a serializable transaction. Exact replay is idempotent; stale and gapped revisions are typed failures and never replace current authorization.

## Metrics boundary

A requested `[from, to)` range is returned only when every date has an explicit aggregate row. `complete` may contain observed zero counts. `partial`, `blocked`, and `rebuilding` contain no counts and carry an explicit unavailable reason. A missing date is typed owner unavailability, not an empty result or fabricated zero.

The persistence model enforces the same value shape: complete rows require every count; incomplete rows forbid every count and require an unavailable reason. The Analytics model contains no raw-IP field or PostgreSQL `inet` column.

## Proof

- domain tests cover listing requirements, placement exposure and sponsored placement identity, campaign parameter allowlisting, event-time bounds, negative metrics, and observed-zero versus unavailable states;
- application tests cover accepted intake, same-payload replay, digest conflict, canonical campaign-parameter ordering, unknown public-read revision rejection, complete aggregate range coverage, canonical public membership ordering, duplicate membership rejection, and access source revision validation;
- infrastructure model tests cover semantic-key uniqueness, event shape and time constraints, restrictive immutable projection ownership, access revision concurrency, incomplete metric value shape, and absence of raw-IP storage.

The infrastructure proof is currently model-level. Real PostgreSQL uniqueness races, serializable access revision ordering, and constraint rejection remain integration-test requirements before the Analytics persistence boundary can be called production-proven.
