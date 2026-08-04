# Aggregator Backend agent routing

This repository owns the Aggregator Backend described in the implementation specification.

Apply the shared general and backend rules supplied by the repository owner. Crypto-specific causal, Train/OOS, market-data, and live-trading rules do not apply to this product.

## Repository invariants

- One production meaning has one canonical owner.
- `Catalog`, `Query`, `Ingestion`, `Analytics`, and `Promotion` remain physically separated bounded contexts.
- A context may consume another context only through producer-owned contracts, generated clients, immutable artifacts, or asynchronous integration events.
- No shared business database, shared `DbContext`, cross-context repository, cross-database SQL, or domain type in `BuildingBlocks`.
- Read paths do not mutate, repair, migrate, publish, rebuild, or process outbox work.
- Missing, unsupported, stale, blocked, and observed-zero states remain distinct.
- Imported collector data can create reviewable proposals and Catalog drafts; it cannot publish.
- Product-specific Berlin recording data belongs only under `product-config/` and fixtures.
- Do not add empty future modules, placeholder services, generic domain utilities, or compatibility branches.
- Work on the existing branch selected by the user. Do not create another branch.

## Proof

Every production change must update its owner tests and, when applicable, architecture, contract, database, integration, migration, generated-artifact, security, or smoke proof.
