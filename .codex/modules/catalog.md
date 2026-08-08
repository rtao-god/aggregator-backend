# Catalog module

## Owner

Catalog is the canonical owner of product configuration, listing identities and immutable revisions, accepted provenance, media lifecycle and publication rights, editorial decisions, listing-scoped claims/access, immutable publications, the current Catalog publication pointer, and emergency public-visibility suppressions including private evidence and their revisioned lifecycle.

## Projects

- `Catalog.Domain`: owner invariants and state transitions; no framework dependencies.
- `Catalog.Contracts`: producer-owned wire contracts, publication artifact, and integration events. Visibility events expose only public target/reason/response state and never private evidence.
- `Catalog.Application`: authored product-configuration source loading, strict source validation, canonical import artifacts, use cases, deterministic serialization, explicit mapping, typed failure translation, event correlation, and canonical payload digests.
- `Catalog.Infrastructure`: EF Core/PostgreSQL persistence, suppression revision persistence, durable outbox rows, S3-compatible publication adapter, and read-only readiness.
- `Catalog.Media.Domain`: media aggregate state and invariants.
- `Catalog.Media.Contracts`: the sole producer-owned contract for resolving one exact publishable media asset revision and public variant.
- `Catalog.Media.Application`: media commands, processing, and the implementation of the producer-owned publication-binding authority.
- `Catalog.Media.Infrastructure`: Catalog media persistence and object-storage adapters; it does not own or reimplement the publication-binding decision.
- `Catalog.Api`: the single authenticated Catalog command transport, including media upload/register/revoke/read routes; thin Controllers, no repository access or domain decisions.
- `Catalog.Worker`: fail-fast Catalog outbox dispatcher; it has no HTTP surface and never applies migrations.
- `Catalog.Media.Worker`: resource-heavy scanning and variant generation inside the Catalog owner boundary; it uses Catalog app credentials and the Catalog media object prefix.
- `Catalog.Migrations`: the only Catalog database migration owner, including media tables, publication media gates, media work leases, and both Catalog outbox schemas.

Catalog-owned media code uses CLR namespaces under `Aggregator.Catalog.Media.*` and diagnostic owners under `Catalog.Media.*`. Former standalone CLR and diagnostic identities without the Catalog module separator are prohibited active paths; transport middleware surfaces the canonical producer owner directly and does not normalize a legacy owner name. Catalog listing use cases depend only on `Catalog.Media.Contracts`; consumer-local media binding ports and Infrastructure-owned authority copies are prohibited.

Catalog geography states describe reusable product meaning: `PrimaryMarket`, `NearbyMarket`, `RemoteOnly`, `OutsideMarket`, and `Unresolved`. Berlin boundaries, zones, labels, and inclusion policy belong to imported product configuration; they cannot become Domain or wire enum names. The generic rename preserves stored numeric identities `1` and `2`, while strict JSON accepts only the current `primaryMarket` and `nearbyMarket` tokens and rejects the former product-specific tokens.

## Active flows

```text
Git-authored product-config directory
→ Catalog.Application strict source loader
→ canonical import contract + locked SHA-256 digest
→ explicit import
→ immutable PostgreSQL configuration revision
→ explicit optimistic activation
→ listing identity
→ immutable listing revision
→ editorial approval
→ deterministic publication artifact
→ verified object storage write
→ atomic publication pointer switch + correlated outbox event
→ bounded Catalog worker lease
→ publisher-confirmed RabbitMQ delivery or explicit dead-letter state
```

The validator CLI is only a composition root over `CatalogProductConfigurationSourceLoader`; it does not define a parallel manifest, parser, semantic validator, or digest formula. Runtime startup never reads or imports product-config files.

```text
exact rollback target publication ID
→ reopen the exact stored artifact
→ verify current publication contract metadata and actual SHA-256 bytes
→ atomic rollback pointer switch + correlated outbox event
```

```text
scoped media upload authorization
→ private quarantine object
→ verified upload completion
→ persisted scanning work lease
→ MIME/hash/dimension/security inspection
→ safe public variants
→ rights-active accepted asset
→ Catalog.Media.Contracts exact binding request
→ Catalog.Media.Application owner validation
→ exact listing-revision media binding
→ publication media gate
```

```text
exact public target
→ requested suppression revision
→ active suppression revision
→ current suppression + both revisions + minimal public event in one transaction
→ Query safety consumer
→ resolved suppression revision + minimal public event
```

The create command intentionally persists both `requested/1` and `active/2` while exposing only the active public event. Resolve requires exact revision `2` and creates `resolved/3`. Listing, media, and contact targets are validated against Catalog-owned current state; route targets require an active publication. Contact IDs are generated by the Catalog application ID owner, included in the immutable listing-revision digest, persisted unchanged, and sealed in publication artifact contract revision `4`. Exact media ID, media aggregate revision, variant ID, object URI, content identity, rights basis, display order, caption, and assertion ID are also canonicalized into the listing-revision digest and sealed in revision `4`. Publication revalidates the current canonical revision digest, so a pre-identity revision with contacts or media cannot be silently republished. External-reference suppression remains fail-closed until stable external-reference identities exist in the Catalog publication contract.

## API boundary

`Catalog.Api` accepts Catalog-owned contracts, resolves an explicit `actor_id` projection from the authenticated principal, requires operation-specific OAuth scopes, rejects numeric enum tokens and unmapped JSON members, and translates known owner failures into RFC 7807 responses. Missing actor mapping fails closed; it never provisions an Actor lazily. HTTP correlation is propagated into every resulting integration event.

Media commands live under `/api/catalog-command/media/assets`. There is no separate media API audience or edge route. The same Catalog command host owns authorization, rate limiting, correlation, strict JSON behavior, and transport failure mapping for media operations.

Visibility commands use the dedicated `catalog.manage-visibility` scope. Admin responses may contain the private evidence reference; the producer event contract cannot contain it.

## Persistence and messaging boundary

Catalog persists business state and the producer-owned event envelope in one PostgreSQL transaction. Rows carry the exact routing key, contract identity, canonical payload digest, correlation/causation, delivery attempts, lease state, dispatch completion, and indivisible dead-letter state. Migration from a legacy JSON outbox fails closed when existing rows cannot prove their original UTF-8 payload bytes.

`Catalog.Migrations/V008__catalog_media_owner_merge.sql` supports a clean Catalog database and a complete prior media schema. It rejects partial legacy schemas, non-empty legacy `jsonb` outboxes, orphan media references, incomplete dead-letter tuples, and incompatible table shapes before transferring ownership. It then establishes the Catalog-owned media FK and publication gate.

`catalog.public-visibility-suppression.changed` carries exact target identity, public reason class, Catalog-selected response mode, lifecycle state, effective interval, and aggregate revision. Private legal evidence and transition notes remain exclusively in Catalog.

## Proof

- Catalog domain invariant tests cover invalid subject kinds, forbidden provenance, optimistic concurrency, scoped access revocation, suppression target/lifecycle rules, and exact media binding identity.
- Catalog application E2E covers configuration import/activation, listing revisions, stable contact identity, publication artifact revision `4`, approval, deterministic publications, exact rollback, exact rollback-artifact verification, and pointer isolation when verification fails.
- Product-configuration tests prove strict authored-source parsing, locked canonical digest, semantic Catalog validation, real PostgreSQL canonical-byte persistence, immutable duplicate rejection, explicit activation, stale pointer rejection, and exact active revision rehydration.
- Catalog geography contract tests prove generic wire tokens, rejection of product-specific/numeric tokens, and stable numeric storage identities.
- Catalog suppression application tests cover requested/active/resolved revision persistence, stale revision rejection, correlation propagation, and absence of private evidence from public events.
- Catalog media domain/application/infrastructure tests cover immutable state transitions, exact command replay, storage verification, work leases, and producer-owned publication eligibility.
- Catalog migration integration tests cover clean creation, partial legacy media rejection, exact outbox text storage, media ownership transfer, FK publication gating, and PostgreSQL constraints.
- Catalog API contract tests cover anonymous liveness, authentication, actor mapping, canonical media owner errors, route/body identity mismatch, enum wire rejection, and the Catalog-owned media endpoints.
- Catalog worker tests cover strict required configuration and bounded transport settings.
- Architecture tests block forbidden project references, product-specific geography tokens in reusable Catalog/Query production source, obsolete media API/migration projects and services, obsolete Catalog Media CLR/diagnostic identities, consumer-local binding ports, Infrastructure authority copies, and business ownership in BuildingBlocks.
