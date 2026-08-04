#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
path = ROOT / "src" / "Catalog" / "Catalog.Media.Infrastructure" / "EfCatalogMediaRepository.cs"
text = path.read_text(encoding="utf-8")
old = '''                    WHERE asset_id = @asset_id
                      AND lease_token = @lease_token
                      AND completed_at_utc IS NULL
                    RETURNING attempt_count;'''
new = '''                    WHERE asset_id = @asset_id
                      AND lease_token = @lease_token
                      AND lease_expires_at_utc > @failed_at_utc
                      AND completed_at_utc IS NULL
                    RETURNING attempt_count;'''
if old not in text:
    raise RuntimeError("Catalog media failure lease anchor is missing.")
path.write_text(text.replace(old, new), encoding="utf-8")
