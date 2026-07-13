---
feature: scenebuilder-core-m0-m2
task: b0-t1
agent: tdd-test-writer
updated: 2026-07-13T18:15:51Z
iteration: 1
---

## Decision
SKIPPED
REASON: Pure project/solution scaffolding — no production types, no branching logic. The
deliverable IS the green gate itself (`dotnet build`/`dotnet test` exit 0 with 0 tests
acceptable); there is no observable behavior to assert beyond build success, which the
gate command already verifies. Matches TEST_RECOMMENDATION: skip and research.md's
SUGGESTED_TESTS: none-recommended.

## Stale tests
PRUNED: none
UPDATED: none
(Repo is greenfield — only specs/, .git, .gitignore exist; no prior solution, csproj, or
test files to check for staleness.)

## Contract
Code-writer must produce, per research.md blueprint:
- `SceneBuilder.sln` at repo root (generated via `dotnet new sln` + `dotnet sln add`, not
  hand-written) referencing both projects.
- `SceneBuilder.Core/SceneBuilder.Core.csproj` — netstandard2.1, Nullable enable,
  PackageReference Microsoft.CodeAnalysis.CSharp 4.9.2, System.Text.Json 8.0.5.
- `SceneBuilder.Core/Compat/IsExternalInit.cs` — internal shim enabling `init`/`record`
  on netstandard2.1 for all downstream tasks.
- Empty `Model/`, `Plan/`, `Identity/`, `Serialization/` folders (`.gitkeep`) under
  SceneBuilder.Core — no other folders (Parsing/Diff/Materialize/Reconcile belong to later
  buckets).
- `SceneBuilder.Core.Tests/SceneBuilder.Core.Tests.csproj` — net8.0, Nullable enable,
  IsPackable false, Microsoft.NET.Test.Sdk 17.9.0, xunit 2.7.0,
  xunit.runner.visualstudio 2.5.7 (PrivateAssets=all), ProjectReference → Core.
- Do NOT create unity/, Editor asmdef, package.json, or Core asmdef.

Verification: `export PATH="$HOME/.dotnet:$PATH" && dotnet build SceneBuilder.sln` succeeds;
`dotnet test SceneBuilder.sln` exits 0 (0 tests acceptable).

STATUS: SKIPPED
