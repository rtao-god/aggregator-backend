# Analytics module

## Owner

Analytics is the canonical owner of accepted public interaction events, their semantic idempotency, traffic-quality state, aggregate readiness, and owner-facing listing metrics. A click remains an interaction; it is never renamed to a lead or conversion without a separate proven source contract.

## Projects

- `Analytics.Domain`: interaction vocabulary, public placement attribution, campaign allowlist, event-time bounds, traffic-quality states, and aggregate readiness semantics.
- `Analytics.Contracts`: public event write contracts and owner metrics read contracts.
- `Analytics.Application`: exact public-read membership validation, anti-abuse verification port, atomic event registration contract, typed failure translation, local projection write contracts, and fail-closed aggregate coverage reads.

Infrastructure, API, worker, and migration projects are not yet active Analytics owners. They must consume these contracts without adding raw-IP domain storage, synchronous Query/Catalog calls, or incomplete-as-zero fallback behavior.

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

## Local projection boundary

Analytics validates interactions and report authorization through local immutable projections; it never calls Query or Catalog synchronously on either path.

- `PublicReadReferenceProjection` carries one exact public-read revision, its three component identities, source publication, content and membership digests, activation time, and canonical sorted public listing membership after safety suppression.
- `ListingMetricsAccessProjection` carries one exact Catalog access revision for an actor and listing, including the source payload digest and `view_listing_analytics` decision.
- Empty or duplicate listing identities, unknown placement exposure values, non-UTC times, malformed digests, and non-positive access revisions fail at the Application/Domain owner boundary.

Infrastructure must apply these projections idempotently, reject same identity/revision with a different digest as corruption, and block stale or gapped access revisions rather than inventing authorization state.

## Metrics boundary

A requested `[from, to)` range is returned only when every date has an explicit aggregate row. `complete` may contain observed zero counts. `partial`, `blocked`, and `rebuilding` contain no counts and carry an explicit unavailable reason. A missing date is typed owner unavailability, not an empty result or fabricated zero.

## Proof

- domain tests cover listing requirements, placement exposure and sponsored placement identity, campaign parameter allowlisting, event-time bounds, negative metrics, and observed-zero versus unavailable states;
- application tests cover accepted intake, same-payload replay, digest conflict, canonical campaign-parameter ordering, unknown public-read revision rejection, complete aggregate range coverage, canonical public membership ordering, duplicate membership rejection, and access source revision validation.
