# Development feedback-loop contract

## Purpose

Repository verification is ordered by diagnostic cost. A cheap structural defect must not wait behind a complete restore, semantic format pass, full build, container build, or end-to-end scenario.

## Canonical gates

1. `.tools/complete-backend.py --check`
   - verifies every physical project and every `ProjectReference`;
   - verifies the complete and runtime solutions;
   - performs no repository mutation in check mode.
2. `Architecture.Tests`
   - verifies context dependency direction, startup/read-path safety, credential hygiene, solution ownership, and read-only CI;
   - has no project references to production assemblies and can build independently.
3. Full solution restore.
4. Whitespace-only format verification.
5. Full compile with warnings as errors.
6. Full tests without rebuilding.
7. Explicit integration, container, and end-to-end proofs at their owner stage.

## Local command selection

- `preflight` is the default before broad work.
- `build-project` and `test-project` are the default during one-owner edits.
- `build-runtime` proves production composition without compiling test projects.
- `test-all` is a task-stage/final gate, not a command to repeat after every edit.
- `format-full-check` is an explicit final code-style/analyzer proof, not an automatic pre-build tax.
- `compose-build` owns image compilation; `compose-up*` owns startup only.

## Performance invariants

- GitHub Actions never commits generated diagnostics or repair output.
- The repository has one automatic workflow.
- A new push cancels the obsolete run for the same ref.
- Automatic CI uses bounded MSBuild concurrency and no node reuse.
- Test execution does not rebuild after a successful build.
- Docker restore/publish uses a persistent BuildKit NuGet cache.
- Agent logs, probes, repair scripts, and proof snapshots are not tracked source.
- A full suite exceeding five minutes without compiler or test progress is treated as a fault to investigate, not as a reason to extend the timeout.
