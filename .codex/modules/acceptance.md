# Acceptance support

## Owner boundary

Acceptance projects provide owner-scoped test composition roots; they do not own production business meaning and are not themselves proof of an end-to-end production path.

- `Acceptance.Contracts` owns only private test-control transport.
- `Acceptance.Control` may compose Catalog Application/Infrastructure and `Acceptance.Contracts` only.
- `Acceptance.Analytics.Control` may compose Analytics Application/Infrastructure and `Acceptance.Contracts` only.
- `Acceptance.Identity` issues local test tokens for exact production audiences and scopes.

The previous executable scenario was removed because it bypassed canonical Ingestion, Catalog event delivery, Query projection, Promotion placement, and media lifecycle owners. It must not return as a compatibility harness.

## Required replacement proof

A production-path E2E suite remains required and must use public or producer-owned boundaries in dependency order:

1. register and upload an `aggregator-candidate-ingestion` package;
2. validate, review, commit, and record exact Catalog outcomes;
3. publish Catalog state and consume the producer event into Query;
4. verify one exact `PublicReadRevision` through the public Query API;
5. record Analytics interactions against that revision and verify readiness/aggregates;
6. create a Promotion placement, consume `promotion.placement.changed`, and verify sponsored/organic composition plus hard expiry;
7. prove replay/conflict behavior and revision isolation without direct database seeding across owners.

Until that suite exists and runs against real PostgreSQL, RabbitMQ, and object storage, the repository must report E2E proof as incomplete.
