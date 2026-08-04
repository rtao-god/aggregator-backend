#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
path = ROOT / "src" / "Catalog" / "Catalog.Media.Application" / "CatalogMediaApplication.cs"
text = path.read_text(encoding="utf-8")
anchor = '''    public static string ComputeDigest<T>(T value) => ComputeDigest(Serialize(value));

    public static string ComputeDigest(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));'''
replacement = '''    public static T Deserialize<T>(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            throw new CatalogMediaApplicationException(
                "CatalogMedia.Serialization",
                "CATALOG_MEDIA_DOCUMENT_EMPTY",
                500,
                "A persisted Catalog media document is empty.",
                "Restore the exact document from a verified backup.");
        }
        try
        {
            return JsonSerializer.Deserialize<T>(value, Options)
                ?? throw new CatalogMediaApplicationException(
                    "CatalogMedia.Serialization",
                    "CATALOG_MEDIA_DOCUMENT_NULL",
                    500,
                    "A persisted Catalog media document deserialized to null.",
                    "Restore the exact document from a verified backup.");
        }
        catch (JsonException exception)
        {
            throw new CatalogMediaApplicationException(
                "CatalogMedia.Serialization",
                "CATALOG_MEDIA_DOCUMENT_INVALID",
                500,
                "A persisted Catalog media document does not satisfy its owner contract.",
                "Restore the exact document from a verified backup.",
                innerException: exception);
        }
    }

    public static string ComputeDigest<T>(T value) => ComputeDigest(Serialize(value));

    public static string ComputeDigest(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));'''
if anchor not in text:
    raise RuntimeError("Catalog media serialization anchor is missing.")
path.write_text(text.replace(anchor, replacement), encoding="utf-8")
