# Analytics module

## Owner

Analytics is the canonical owner of accepted public interaction events, semantic idempotency, traffic-quality state, public-reference validation, listing-metrics authorization projections, daily aggregate readiness, and owner-facing listing metrics. A click remains an interaction; it is never renamed to a lead or conversion without a separate proven source contract.

## Projects

- `Analytics.Domain`: interaction vocabulary, placement attribution, campaign allowlist, event-time bounds, traffic-quality states, and aggregate readiness semantics.
- `Analytics.Contracts`: public interaction write contracts and owner metrics read contracts.
- `Analytics.Application`: exact public-read membership validation, anti-abuse verification port, atomic event registration contract, local projection write contracts, closed-range aggregate materialization contract, typed failure translation, and fail-closed aggregate coverage reads.
- `Analytics.Infrastructure`: EF Core/PostgreSQL persistence for accepted events, immutable public-read membership, listing access revisions, complete daily aggregates, UUIDv7 identity, and read-only DB readiness.
- `Analytics.Api`: public anti-abuse proof and interaction intake plus authenticated listing-metrics reads; strict JSON, typed access failures, resource authorization, bounded request/rate limits, and read-only health.
- `Analytics.Worker`: bounded rebuild of closed UTC daily aggregate ranges; repeated owner failure terminates the worker rather than producing an infinite retry loop.
- `Analytics.Migrations`: one-shot owner migrations. API and worker startup never apply DDL.

The obsolete parallel `AnalyticsRuntime*` contracts, service, persistence path, tests, and source generator were removed. They duplicated event and metric ownership, fabricated missing metrics as zero, and exposed a second incompatible wire contract.

## Intake flow

```text
anti-abuse proof request
→ short-lived HMAC proof bound to client event ID and occurrence time
→ public interaction request
→ exact semantic key: client event ID + event kind
→ canonical payload digest
→ prior-result or digest-conflict check
→ domain and timestamp validation
→ anti-abuse verification
→ Analytics-local public-read membership validation
→ atomic event registration
→ accepted or exact already-applied response
```

The anti-abuse token is transport proof and is not persisted in the event payload digest. Repeating the same semantic event and business payload returns the original result even when the short-lived proof rotated. Reusing the semantic key with a different business payload is a blocking conflict.

`Analytics.Infrastructure` persists the event and its allowlisted campaign parameters in one transaction. PostgreSQL owns the unique semantic key `(client_event_id, event_kind)`, event-time bounds, listing/placement shape, enum ranges, and payload-digest shape. A unique-key race resolves to the exact prior event; another digest is a conflict.

## Local projection boundary

Analytics validates interactions and report authorization through local immutable projections; it never calls Query or Catalog synchronously on either path.

- `PublicReadReferenceProjection` carries one exact public-read revision, its three component identities, source publication, content and membership digests, activation time, and canonical sorted public listing membership after safety suppression.
- `ListingMetricsAccessProjection` carries one exact Catalog access revision for an actor and listing, including source payload digest and `view_listing_analytics` decision.
- Public-read identities are immutable. Same identity with another component or membership digest is corruption.
- Access revisions are applied under a serializable transaction. Exact replay is idempotent; stale and gapped revisions are typed failures.

## Aggregate boundary

The worker rebuilds an explicit closed UTC range `[from, to)`, at most 31 days. The aggregate writer:

```text
loads exact public-read activation intervals
→ loads exact listing memberships
→ validates every accepted event against its active revision and membership
→ counts organic and sponsored impressions separately
→ computes a deterministic source digest
→ writes complete daily rows, including observed zero
→ removes stale rows only inside the exact rebuilt range
→ commits atomically
```

A requested metrics range is returned only when every date has an explicit aggregate row. `complete` may contain observed zero counts. `partial`, `blocked`, and `rebuilding` contain no counts and carry an explicit unavailable reason. A missing date is typed owner unavailability, not an empty result or fabricated zero.

## Persistence and migration boundary

The canonical database schemas are:

```text
events
access_projection
aggregates
```

The migration from the obsolete `analytics` schema fails closed when legacy rows exist because the old event/metric vocabulary cannot be silently converted into the canonical owner contract. Runtime hosts use only the app role; the migration host owns DDL.

## Proof

- domain tests cover listing requirements, placement exposure, campaign parameter allowlisting, event-time bounds, negative metrics, and observed-zero versus unavailable states;
- application tests cover accepted intake, exact replay, digest conflict, canonical parameter ordering, unknown public-read revision rejection, complete aggregate range coverage, canonical public membership ordering, and access source-revision validation;
- API tests cover anonymous liveness, exact anti-abuse binding, semantic replay, unknown-member and numeric-enum rejection, authentication, actor mapping, observed zero, and missing aggregate coverage;
- infrastructure model tests cover semantic-key uniqueness, event shape and time constraints, restrictive immutable projection ownership, access revision concurrency, incomplete metric value shape, and absence of raw-IP storage;
- worker configuration and aggregate persistence require focused build/test proof before Analytics can be declared green.

Real PostgreSQL uniqueness races, serializable projection ordering, migration execution, and aggregate rebuild remain integration-proof requirements for the repository-wide acceptance stage.
