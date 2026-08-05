# ADR 0001: Caddy owns the local reverse-proxy boundary

- **Status:** accepted
- **Decision owner:** `compose.yaml` and `deploy/Caddyfile`

## Decision

The canonical local deployment uses one Caddy container. It is the only container with a host port, bound to `127.0.0.1`. Caddy routes explicit API prefixes to internal services, emits basic response-security headers, and returns `404` for every unowned path.

Application contracts do not depend on Caddy-specific domain behavior. TLS certificates, public hostnames, external identity topology, and production ingress remain deployment concerns, but any replacement must preserve the same route allowlist, forwarded-header validation, body limits, health semantics, and single-edge exposure.

## Rejected alternatives

- Direct host ports per API create multiple ingress owners and bypass one policy boundary.
- Keeping Caddy, nginx, and overlay Compose files in parallel creates deployment drift.
- Selecting a proxy at runtime from mutable configuration makes startup topology non-reproducible.
