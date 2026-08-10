# Catalog universality proof

Status: proven by repository tests

## Decision

Catalog remains a reusable product owner. A second business vertical must be introduced as product configuration and must not create a new service, database, domain aggregate, endpoint family, or vertical-specific branch in reusable production code.

The repository contains two deliberately different configuration sets:

```text
product-config/berlin-recording/
test-fixtures/product-config/berlin-coworking-spaces/
```

The coworking fixture is acceptance evidence, not production startup data. It uses different Catalog, category, attribute, label, and route identities while retaining the same generic Site/Catalog/Taxonomy/Attribute/Geography contracts.

## Required owner path

Both verticals use the same path:

```text
Git-authored product-config files
→ CatalogProductConfigurationSourceLoader
→ strict syntax and semantic validation
→ canonical import artifact and SHA-256 digest
→ Catalog configuration import
→ immutable Catalog configuration revision
→ explicit optimistic activation
→ exact active revision rehydration
```

No alternate coworking loader, mapper, API, repository, migration stream, worker, or database is permitted.

## Physical proof

The second vertical is proven at three levels:

1. `SecondCatalogProductConfigurationTests` runs the same source loader and semantic validator and verifies the contrasting Catalog/category identities.
2. `SecondCatalogProductConfigurationPersistenceTests` runs the same PostgreSQL import, activation, immutable persistence, and exact rehydration path.
3. `CatalogUniversalityReachabilityTests` rejects coworking business literals in reusable `src`, rejects coworking deployables/database owners, and requires the two tests above to use the canonical owner path.

## Boundary rules

Allowed locations for second-vertical identities:

```text
test-fixtures/product-config/berlin-coworking-spaces/
tests/ that explicitly prove universality
this decision document
```

Forbidden locations:

```text
src/**
production Compose service/database identities
BuildingBlocks
shared Domain enums or boolean flags
Catalog-specific controllers/services/repositories
```

Examples of prohibited shortcuts:

```text
CoworkingCatalogService
CoworkingSpace aggregate in reusable core
is_coworking_space
coworking_db
coworking-api
if (catalogKey == "berlin-coworking-spaces")
```

## Acceptance

The universality claim remains valid only while all of the following pass:

```text
Catalog.Application.Tests second-vertical validation
Catalog.Infrastructure.Tests second-vertical PostgreSQL persistence
Architecture.Tests CatalogUniversalityReachabilityTests
full AggregatorBackend.slnx build and test run
```

A future vertical must add another configuration/acceptance fixture and may extend generic contracts only when the new meaning is genuinely reusable and versioned. It must not fork the existing vertical or introduce a business-specific compatibility path.
