# Repository tooling

## Owner

`docs/architecture/project-topology.json` is the only owner of the approved .NET project graph. Repository tooling validates physical files against that contract and renders canonical solutions and inventory; filesystem discovery is never an implicit inclusion rule.

## Canonical paths

- `docs/architecture/project-topology.json`
- `.tools/complete-backend.py`
- `AggregatorBackend.slnx`
- `AggregatorBackend.Runtime.slnx`
- `docs/decisions/project-inventory.md`
- `tools/repo.ps1`
- `.github/workflows/ci.yml`
- `tests/Architecture.Tests/RepositoryAutomationRulesTests.cs`
- `docs/decisions/development-feedback-loop.md`

## Invariants

- Every physical `.csproj` under `src/` or `tests/` is explicitly approved by the topology manifest.
- `AggregatorBackend.slnx` contains exactly all approved projects.
- `AggregatorBackend.Runtime.slnx` contains exactly the approved production subset.
- Unknown, missing, forbidden, duplicate, or unapproved projects fail before restore.
- Every `ProjectReference` resolves to an approved physical project.
- Obsolete owner contours, generators, endpoints, namespaces, and deployment references are rejected repository-wide.
- No second complete solution exists.
- CI is read-only, has one automatic workflow, and runs topology/architecture proof before full restore.
- Local owner-level build/test commands are preferred during focused work; broad build/test is a stage-boundary proof.
- Compose startup never implies image rebuild.
- `.codex/ci`, `.codex/probes`, `.codex/tmp`, and repair scripts are transient and never tracked.
