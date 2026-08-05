# Acceptance orchestration

## Owner boundary

Acceptance projects prove cross-context behavior but do not own production business meaning.

- `Acceptance.Contracts` owns only the private test-control transport used between acceptance composition roots and the runner.
- `Acceptance.Control` is the Catalog-only test composition root. It may reference Catalog Application/Infrastructure and `Acceptance.Contracts`; it must not reference another bounded context implementation.
- `Acceptance.Analytics.Control` is the Analytics-only test composition root. It may reference Analytics Application/Infrastructure and `Acceptance.Contracts`; it must not reference Catalog or Query implementations.
- `Acceptance.Identity` issues local test tokens for exact production audiences and scopes.
- `Acceptance.Runner` orchestrates public APIs and private test-control endpoints. It consumes producer-owned Contracts and must not reference production Infrastructure.

## Canonical scenario

The runner proves:

1. collector candidate replay and conflict;
2. Catalog publication and Query projection;
3. Analytics public-reference/access bootstrap;
4. `listing_impression`, `listing_opened`, and `website_clicked` with exact anti-abuse proof;
5. Analytics replay/conflict and a closed UTC daily aggregate;
6. authorized metrics read with explicit readiness;
7. Promotion overlay publication, revision isolation, rollback, and explicit republish.

Missing numeric values are never replaced with zero. A test-control project may seed only deterministic test state required to reach a production owner boundary; it may not implement production formulas or duplicate production contracts.
