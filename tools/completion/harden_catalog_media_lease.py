#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
path = ROOT / "src" / "Catalog" / "Catalog.Media.Application" / "CatalogMediaApplication.cs"
text = path.read_text(encoding="utf-8")
old = '''public sealed record CatalogMediaProcessingLease(
    Guid AssetId,
    Guid LeaseToken,
    int AttemptCount,
    DateTimeOffset LeaseExpiresAtUtc,
    CatalogMediaAsset Asset);'''
new = '''public sealed record CatalogMediaProcessingLease(
    Guid AssetId,
    Guid LeaseToken,
    int AttemptCount,
    DateTimeOffset LeaseExpiresAtUtc,
    long StoredAggregateRevision,
    CatalogMediaAsset Asset);'''
if old not in text:
    raise RuntimeError("Catalog media processing lease anchor is missing.")
path.write_text(text.replace(old, new), encoding="utf-8")
