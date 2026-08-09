# Catalog listing dispute owner

Status: active

## Decision

Catalog owns listing disputes because the disputed meaning is the canonical listing identity/content lifecycle, not Promotion presentation or Query projection state.

```text
review-authorized Catalog command
→ open immutable dispute evidence
→ one current Open dispute per listing
→ Catalog listing-promotion eligibility event with hasBlockingDispute=true
→ Promotion local projection
→ automatic pause of scheduled/active paid placements
```

```text
review-authorized Catalog command
→ exact Open dispute revision
→ immutable Open-to-Resolved transition
→ Catalog listing-promotion eligibility event with hasBlockingDispute=false
→ Promotion local projection
```

Resolution never resumes a paid placement automatically. Promotion resume remains an explicit command that rechecks the current entitlement, product, time window, capacity and Catalog-derived eligibility projection.

## Boundary

Catalog stores:

- dispute ID and listing ID;
- immutable opening reason, actor and timestamp;
- explicit lifecycle state;
- immutable resolution reason, actor and timestamp;
- aggregate revision.

Catalog does not store Promotion placement IDs, capacity slots or paid presentation state. Promotion does not store dispute evidence and never calls Catalog synchronously.

Catalog publishes only the already-defined minimal `ListingPromotionEligibilityChanged` contract. `hasBlockingDispute` is a Catalog-selected fact in that event; Promotion consumes it without reconstructing Catalog lifecycle rules.

## Concurrency and audit

- Opening requires the exact current listing version.
- Only one Open dispute is permitted for a listing.
- Resolving requires the exact dispute aggregate revision.
- Opening evidence is immutable.
- A Resolved dispute is immutable and cannot be reopened.
- A later dispute uses a new dispute identity.
- Open and resolve persist the dispute transition and eligibility outbox event in the same serializable Catalog transaction.

## Publication safety

A listing with an Open dispute cannot enter a newly activated publication and cannot be reintroduced by rollback.

The gate exists twice:

1. Catalog application persistence locks every selected listing and checks Open disputes before publication/rollback activation.
2. PostgreSQL protects `current_catalog_publication` with a trigger that rejects a pointer switch whose target publication contains an Open dispute.

The database raises SQLSTATE `P7604`, translated to the typed Catalog failure `CATALOG_PUBLICATION_LISTING_DISPUTED`.

An already active public listing is suppressed from paid promotion immediately through the eligibility event. Public visibility itself remains a separate Catalog visibility-suppression owner; a dispute does not silently invent legal removal semantics.

## HTTP contract

```http
POST /api/catalog-command/listings/{listingId}/disputes
POST /api/catalog-command/listings/{listingId}/disputes/{disputeId}/resolution
```

Both commands require `catalog.review`, authenticated actor mapping, correlation propagation and strict JSON behavior.

## Proof

Required proof includes:

- domain tests for Open/Resolved lifecycle and revision conflict;
- application tests for exact listing/dispute preconditions;
- API tests for authorization and typed not-found behavior;
- EF model proof for the filtered unique Open-dispute index and listing FK;
- migration proof for immutable audit and the pointer activation trigger;
- source/architecture proof that both publication create and rollback use the application gate;
- dependency proof that Catalog does not reference Promotion projects or implementations.
