#!/usr/bin/env python3
"""Normalize the Ingestion registration/upload/validation/review/commit slice.

The script contains only deterministic compatibility edits. It is idempotent and is executed
before the full repository build/test gate; no mutation is committed when the gate fails.
"""

from __future__ import annotations

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]


def update(path: pathlib.Path, transform, check: bool) -> bool:
    if not path.exists():
        raise RuntimeError(f"Required file is missing: {path.relative_to(ROOT)}")
    original = path.read_text(encoding="utf-8")
    changed = transform(original)
    if changed == original:
        return False
    if check:
        print(f"stale: {path.relative_to(ROOT).as_posix()}", file=sys.stderr)
        return True
    path.write_text(changed, encoding="utf-8", newline="\n")
    print(f"updated: {path.relative_to(ROOT).as_posix()}")
    return True


def normalize_shared(text: str) -> str:
    replacements = (
        ("IngestionItemDecisionContract", "IngestionWorkflowItemDecisionContract"),
        ("IngestionCatalogDeliveryOutcomeContract", "IngestionWorkflowCatalogOutcomeContract"),
        ("IngestionCatalogDeliveryOutcome", "IngestionWorkflowCatalogDeliveryOutcome"),
        (".Order(StringComparer.Ordinal)", ".OrderBy(value => value, StringComparer.Ordinal)"),
        (
            "Enum.IsDefined(item.Outcome)",
            "Enum.IsDefined(typeof(IngestionWorkflowCatalogOutcomeContract), item.Outcome)",
        ),
        (
            "            expectedItemCount: 3,\n            new string('b', 64),",
            "            3,\n            new string('b', 64),",
        ),
        (
            "                        CatalogListingRevisionId: null,\n                        \"catalog.validation_failed\"),",
            "                        CatalogListingRevisionId: null,\n                        FailureCode: \"catalog.validation_failed\"),",
        ),
        (
            "            rejectedItemCount: 1,\n            batch.AggregateRevision,\n            timestamp);",
            "            rejectedItemCount: 1,\n            expectedAggregateRevision: batch.AggregateRevision,\n            changedAtUtc: timestamp);",
        ),
    )
    for old, new in replacements:
        text = text.replace(old, new)
    return text


def normalize_validator(text: str) -> str:
    text = normalize_shared(text)
    text = text.replace("validatedItems.Count !=", "validatedItems.Length !=")
    return text


def normalize_object_reader(text: str) -> str:
    text = normalize_shared(text)
    return text.replace(
        "objectKey.EndsWith('/', StringComparison.Ordinal)",
        "objectKey.EndsWith(\"/\", StringComparison.Ordinal)",
    )


def normalize_worker_program(text: str) -> str:
    text = normalize_shared(text)
    text = text.replace(
        "        builder.Services.AddHostedService<IngestionPackageWorkerService>();",
        "        builder.Services.AddIngestionWorker();",
    )
    return text


def ensure_object_storage_reference(text: str) -> str:
    reference = (
        '    <ProjectReference Include="../../BuildingBlocks/Platform.ObjectStorage/'
        'Platform.ObjectStorage.csproj" />'
    )
    if reference in text:
        return text
    marker = "  </ItemGroup>\n</Project>"
    if marker not in text:
        raise RuntimeError("Cannot locate Ingestion.Infrastructure project-reference group.")
    return text.replace(marker, f"{reference}\n  </ItemGroup>\n</Project>")


def normalize_test(text: str) -> str:
    text = normalize_shared(text)
    # Named arguments must remain named after the first named argument.
    text = text.replace(
        "            payloadObjectSize: 1024,\n            \"application/json\",\n            timestamp);",
        "            payloadObjectSize: 1024,\n            payloadContentType: \"application/json\",\n            registeredAtUtc: timestamp);",
    )
    return text


def main() -> int:
    check = "--check" in sys.argv[1:]
    targets = {
        ROOT / "src/Ingestion/Ingestion.Contracts/IngestionReviewCommitContracts.cs": normalize_shared,
        ROOT / "src/Ingestion/Ingestion.Application/IngestionReviewCommitWorkflow.cs": normalize_shared,
        ROOT / "src/Ingestion/Ingestion.Infrastructure/EfIngestionReviewCommitRepository.cs": normalize_shared,
        ROOT / "src/Ingestion/Ingestion.Api/IngestionReviewCommitController.cs": normalize_shared,
        ROOT / "tests/Ingestion/Ingestion.Application.Tests/IngestionReviewCommitWorkflowTests.cs": normalize_test,
        ROOT / "src/Ingestion/Ingestion.Application/IngestionPackagePayloadValidator.cs": normalize_validator,
        ROOT / "src/Ingestion/Ingestion.Infrastructure/IngestionPackageObjectReader.cs": normalize_object_reader,
        ROOT / "src/Ingestion/Ingestion.Infrastructure/EfIngestionPackageWorkRepository.cs": normalize_shared,
        ROOT / "src/Ingestion/Ingestion.Worker/Program.cs": normalize_worker_program,
        ROOT / "tests/Ingestion/Ingestion.Application.Tests/IngestionPackageProcessingTests.cs": normalize_test,
        ROOT / "src/Ingestion/Ingestion.Infrastructure/Ingestion.Infrastructure.csproj": ensure_object_storage_reference,
    }
    stale = False
    for path, transform in targets.items():
        stale |= update(path, transform, check)
    return 1 if check and stale else 0


if __name__ == "__main__":
    raise SystemExit(main())
