# ADR 0001: reverse proxy remains deployment-selected

- **Status:** deferred
- **Decision owner:** deployment topology

The backend contracts do not depend on Caddy, Traefik, or Nginx behavior beyond standard HTTP forwarding, forwarded-header validation, body limits, TLS termination, and route allowlists. The exact production reverse proxy remains deferred until deployment infrastructure is selected.

Local Compose exposes services directly for proof. No empty proxy service or provider-specific production contract is created before that decision has an owner.
