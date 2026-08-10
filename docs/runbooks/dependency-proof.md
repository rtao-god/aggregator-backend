# Dependency audit and SBOM proof

## Purpose

This proof binds the resolved .NET dependency graph to an exact clean repository commit. It combines:

```text
NuGet restore audit
→ direct and transitive package inventory
→ zero-vulnerability gate
→ commit-bound CycloneDX 1.6 SBOM
```

Canonical dependency owners remain:

```text
AggregatorBackend.slnx
global.json
Directory.Packages.props
NuGet restore/package-list commands
```

`tools/run-dependency-proof.py` orchestrates those owners and records evidence. It does not infer dependencies by crawling `obj/project.assets.json`, package caches or project directories.

## Prerequisites

- .NET SDK pinned by `global.json`.
- Python 3.11 or newer.
- Network access to the configured NuGet sources unless all required packages and advisory metadata are already available locally.
- A committed, clean repository tree for release evidence.

## Static self-test

```bash
python tools/run-dependency-proof.py --self-test
```

The self-test uses a synthetic NuGet JSON document. It verifies:

- direct/transitive package normalization;
- duplicate package/version coalescing;
- direct dependency precedence;
- vulnerability counting;
- CycloneDX component identity and uniqueness.

It does not access NuGet or build the solution.

## Full proof

Linux/macOS:

```bash
python tools/run-dependency-proof.py
```

Windows PowerShell:

```powershell
./tools/run-dependency-proof.ps1
```

Execution order:

```text
exact Git commit and clean-tree validation
→ dotnet restore with NuGetAuditMode=all
→ NU1901..NU1904 promoted to errors
→ dotnet package list --include-transitive --format json
→ normalized direct/transitive inventory
→ CycloneDX 1.6 SBOM generation
→ dotnet package list --vulnerable --include-transitive --format json
→ explicit zero-vulnerability assertion
```

The resolved package inventory returned by `dotnet package list` is the only package source used by the proof.

## Audit policy

Restore is executed with:

```text
NuGetAudit=true
NuGetAuditMode=all
WarningsAsErrors=NU1901;NU1902;NU1903;NU1904
```

The additional vulnerability inventory must contain zero vulnerability records. A successful command with one or more returned vulnerabilities still fails the proof.

The result is point-in-time evidence. Advisory data can change without a repository commit, so release verification must use the report created for the actual release decision rather than an older report.

## Execution bounds

Default per-command timeout:

```text
1800 seconds
```

Override:

```bash
python tools/run-dependency-proof.py \
  --command-timeout-seconds 3600
```

Accepted range:

```text
60..7200 seconds
```

A timeout terminates the complete child process tree and is retained as exit code `124`.

## Inventory semantics

A component identity is:

```text
normalized NuGet package ID + exact resolved version
```

If the same package/version is direct in any project, its repository-level classification is `direct`; otherwise it is `transitive`. Each component records the sorted set of referencing project paths.

The inventory is flattened intentionally. It proves the complete resolved component set but does not claim to preserve every framework-specific dependency edge from NuGet's internal graph.

## SBOM semantics

The generated file is CycloneDX 1.6 JSON:

```text
aggregator-backend.cdx.json
```

It contains:

- one application component for the exact source commit;
- one library component per resolved package/version;
- NuGet package URLs;
- direct/transitive classification;
- referencing project paths;
- stable component ordering;
- a UUID derived from the exact source commit.

The SBOM is commit-bound and deterministically ordered. Its metadata timestamp records the actual proof time, so byte-for-byte equality across separate executions is not claimed.

## Evidence

A timestamped directory is written under:

```text
artifacts/dependency-proof/<UTC timestamp with microseconds>/
```

It contains:

```text
dependency-proof.json
dependency-inventory.json
aggregator-backend.cdx.json
01-restore-with-nuget-audit.log
02-capture-resolved-package-inventory.log
03-audit-resolved-package-vulnerabilities.log
```

Schema identity:

```text
aggregator-backend/dependency-proof@1
```

The report records:

- exact source commit and clean-tree state;
- `passed`, `diagnostic` or `failed` status;
- `release_valid` flag;
- SHA-256 of the solution, SDK contract and central package contract;
- all three command records and log digests;
- inventory and SBOM paths and SHA-256 digests;
- total, direct and transitive component counts;
- vulnerability count;
- explicit failure detail.

## Diagnostic dirty-tree override

For local diagnosis only:

```bash
python tools/run-dependency-proof.py --allow-dirty
```

A successful dirty-tree run is reported as:

```json
{
  "status": "diagnostic",
  "release_valid": false
}
```

It is not release evidence.

## Failure handling

1. Open the exact report path printed by the proof.
2. Inspect the referenced command log.
3. For a vulnerable component, identify the nearest direct package owner and upgrade or remove it through central package management.
4. Do not suppress NU1901..NU1904 merely to obtain a green report.
5. Commit the correction.
6. Run a new proof from the corrected commit.

Do not edit inventory/SBOM files, substitute a package-cache scan or reuse a report from another commit.

## Acceptance

Dependency evidence is release-valid only when:

- report `status` is `passed`;
- `release_valid` is `true`;
- source tree is clean;
- source commit equals the release commit;
- NuGet restore audit exits zero;
- resolved package inventory exits zero and contains at least one component;
- vulnerability inventory exits zero with count `0`;
- direct plus transitive count equals total component count;
- inventory and SBOM files exist and match their recorded SHA-256 digests;
- SBOM is CycloneDX 1.6 and contains exactly the reported component set;
- no command timed out;
- source, migration, runtime and backup/restore proofs pass independently.
