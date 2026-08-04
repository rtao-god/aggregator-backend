from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def update(relative: str, transform) -> None:
    path = ROOT / relative
    source = path.read_text(encoding="utf-8")
    result = transform(source)
    if result != source:
        path.write_text(result, encoding="utf-8")


def normalize_processing_document(source: str) -> str:
    replacement = '''internal static class ProcessingDocument
{
    public static byte[] Serialize<T>(T value) =>
        IngestionCanonicalJson.Serialize(value);

    public static T Deserialize<T>(ReadOnlySpan<byte> document) =>
        IngestionCanonicalJson.Deserialize<T>(document);

    public static string ComputeDigest(ReadOnlySpan<byte> document) =>
        IngestionCanonicalJson.ComputeDigest(document);
}

internal sealed class ProcessingImportBatchRow'''
    pattern = re.compile(
        r'internal static class ProcessingDocument\s*\{.*?\}\s*\n\s*internal sealed class ProcessingImportBatchRow',
        re.DOTALL,
    )
    if pattern.search(source):
        return pattern.sub(replacement, source, count=1)
    if replacement in source:
        return source
    raise RuntimeError("ProcessingDocument normalization anchor was not found.")


def normalize_ingestion_tests(source: str) -> str:
    source = source.replace(
        "        Assert.Equal(3, result.Decisions.Count);",
        "        Assert.Collection(result.Decisions, _ => { }, _ => { }, _ => { });",
    )
    source = source.replace(
        "        Assert.All(result.Decisions, decision => Assert.False(string.IsNullOrWhiteSpace(decision.ItemDigest)));",
        "        Assert.All(result.Decisions, decision => Assert.NotEmpty(decision.ItemDigest));",
    )
    source = source.replace(
        "            return Task.FromResult(ValidationResult);",
        "            return Task.FromResult(ValidationResult!);",
    )
    return source


def normalize_api_tests(source: str) -> str:
    source = source.replace(
        "        Assert.Equal(CatalogIngestionOutcomeStateContract.DraftCreated, first.State);",
        "        Assert.Equal(CatalogIngestionOutcomeStateContract.DraftCreated, first!.State);",
    )
    return source


def main() -> None:
    update(
        "src/Ingestion/Ingestion.Infrastructure/IngestionProcessingPersistence.cs",
        normalize_processing_document,
    )
    update(
        "tests/Ingestion/Ingestion.Processing.Tests/IngestionProcessingTests.cs",
        normalize_ingestion_tests,
    )
    update(
        "tests/Catalog/Catalog.Ingestion.Api.Tests/CatalogIngestionApiContractTests.cs",
        normalize_api_tests,
    )


if __name__ == "__main__":
    main()
