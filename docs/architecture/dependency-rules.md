# Dependency rules

## Inside one bounded context

```text
Domain          → no context project
Contracts       → no Domain/Application/Infrastructure project
Application     → Domain, own Contracts
Infrastructure  → Application, Domain, technical BuildingBlocks
Api             → Application, Infrastructure, own Contracts, technical BuildingBlocks
Worker          → Application, Infrastructure, own Contracts, technical BuildingBlocks
Migrations      → technical persistence only and owner SQL resources
```

## Between bounded contexts

Only producer-owned `*.Contracts`, generated HTTP clients, generated message clients, and schema artifacts may cross a context boundary. Cross-context Domain, Application, Infrastructure, or database references are forbidden.

## BuildingBlocks

BuildingBlocks contain only technical primitives such as correlation, problem details, clocks, UUIDv7 creation, migration execution, message envelopes, outbox transport, object storage transport, authentication bootstrap, observability bootstrap, and test infrastructure. Business entities and state machines belong to their context.

`Architecture.Tests` parses every project reference and source namespace to block invalid dependency edges.
