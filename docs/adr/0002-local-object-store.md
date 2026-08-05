# ADR 0002: SeaweedFS is the local S3-compatible adapter

- **Status:** accepted
- **Decision owner:** `compose.yaml`

## Decision

Local runtime uses one SeaweedFS service with the S3 endpoint on the internal Docker network. Catalog publication artifacts, Catalog media, and Ingestion packages use separate buckets and access the service only through the existing S3-compatible ports.

SeaweedFS is an infrastructure adapter, not a domain owner. Application and domain projects remain unaware of SeaweedFS commands, filesystem paths, or container topology. A managed S3 provider may replace it only after the same object-store capability and integrity contracts pass.

## Rejected alternatives

- MinIO is not retained as a parallel local owner.
- Direct host-filesystem paths would make workers and APIs depend on one machine layout.
- Provider-specific APIs are not introduced without an explicit capability contract.
