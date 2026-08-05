# Repository tooling

## Owner

Repository tooling owns the physical .NET project inventory, canonical solution files, local verification commands, automatic CI order, and separation between image build and runtime startup.

## Canonical paths

- `.tools/complete-backend.py`
- `AggregatorBackend.slnx`
- `AggregatorBackend.Runtime.slnx`
- `tools/repo.ps1`
- `.github/workflows/ci.yml`
- `tests/Architecture.Tests/RepositoryAutomationRulesTests.cs`
- `docs/decisions/development-feedback-loop.md`

## Invariants

- `AggregatorBackend.slnx` contains every `.csproj` under `src/` and `tests/`.
- `AggregatorBackend.Runtime.slnx` contains every `.csproj` under `src/` and no test project.
- No second complete solution exists.
- CI is read-only, has one automatic workflow, and runs inventory/architecture proof before full restore.
- Local project-level build/test commands are preferred during one-owner work.
- Full build/test and full semantic formatting are stage-boundary proofs.
- Compose startup never implies image rebuild.
- `.codex/ci`, `.codex/probes`, `.codex/tmp`, and repair scripts are transient and never tracked.
