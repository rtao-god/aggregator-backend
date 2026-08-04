# Canonical owner map

| Meaning | Canonical owner | Required proof |
|---|---|---|
| Site, Catalog, taxonomy, attributes | Catalog | Domain, config validation, database constraints |
| Organization, Place, Provider | Catalog | Domain and immutable revision tests |
| Listing lifecycle and revision | Catalog | State-machine, concurrency, persistence tests |
| Accepted provenance and media rights | Catalog | Publication-gate tests |
| Publication and current publication pointer | Catalog | Determinism, storage verification, atomic switch tests |
| Import batch and item decision | Ingestion | Integrity and explicit-ledger tests |
| Catalog matching proposal | Ingestion | Deterministic-key and fuzzy-review tests |
| Public listing document, facets, rank, SEO | Query | Projection completeness and search tests |
| Current public-read revision | Query | Composite-revision and concurrency tests |
| Interaction event and aggregate readiness | Analytics | Dedupe, privacy, and aggregate tests |
| Promotion entitlement and sponsored placement | Promotion | Eligibility, capacity, and time tests |
| Raw HTML and collector candidate | Data Collection Platform | Cross-repository sealed export contract |

Transport models, database rows, generated artifacts, tests, caches, and frontend projections do not become additional owners of these meanings.
