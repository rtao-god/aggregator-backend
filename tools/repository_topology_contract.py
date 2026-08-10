"""Canonical technical deployable identities used by repository proof tooling.

This module owns only repository/deployment topology. Business ownership remains
inside the five bounded contexts.
"""

from __future__ import annotations

CANONICAL_CONTEXTS: tuple[str, ...] = (
    "catalog",
    "query",
    "ingestion",
    "analytics",
    "promotion",
)
CANONICAL_MIGRATION_SERVICES: tuple[str, ...] = tuple(
    f"{context}-migrate" for context in CANONICAL_CONTEXTS
)
CANONICAL_RUNTIME_SERVICES: tuple[str, ...] = (
    "catalog-api",
    "catalog-worker",
    "catalog-media-worker",
    "query-api",
    "query-worker",
    "ingestion-api",
    "ingestion-worker",
    "analytics-api",
    "analytics-worker",
    "promotion-api",
    "promotion-worker",
    "reverse-proxy",
)
CANONICAL_API_SERVICES: frozenset[str] = frozenset(
    {
        "catalog-api",
        "query-api",
        "ingestion-api",
        "analytics-api",
        "promotion-api",
    }
)


def migration_service_for_context(context: str) -> str:
    normalized = context.strip().lower()
    if normalized not in CANONICAL_CONTEXTS:
        allowed = ", ".join(CANONICAL_CONTEXTS)
        raise ValueError(
            f"Unknown repository context '{context}'. Allowed values: {allowed}."
        )
    return f"{normalized}-migrate"
