---
feature: scenebuilder-core-m0-m2
task: b0-t1
agent: tdd-code-writer
updated: 2026-07-13T18:17:27Z
iteration: 1
---

## Implementation
FILES:
  - SceneBuilder.sln (generated via `dotnet new sln` + `dotnet sln add`)
  - SceneBuilder.Core/SceneBuilder.Core.csproj
  - SceneBuilder.Core/Compat/IsExternalInit.cs
  - SceneBuilder.Core/Model/.gitkeep
  - SceneBuilder.Core/Plan/.gitkeep
  - SceneBuilder.Core/Identity/.gitkeep
  - SceneBuilder.Core/Serialization/.gitkeep
  - SceneBuilder.Core.Tests/SceneBuilder.Core.Tests.csproj

SUMMARY: Generated the solution and both projects via `dotnet new sln`, `dotnet new classlib`,
`dotnet new xunit`, `dotnet sln add` (per research's mandate to never hand-write the .sln). Removed
the template's default `Class1.cs` and `UnitTest1.cs` (no production types/tests belong in this
task). Set SceneBuilder.Core.csproj to netstandard2.1, Nullable enable, LangVersion latest, with
PackageReference Microsoft.CodeAnalysis.CSharp 4.9.2 and System.Text.Json 8.0.5. Added the internal
`IsExternalInit` compat shim in Compat/ so `record`/`init` compile on netstandard2.1 for every
downstream task. Created the four empty M0 source folders with `.gitkeep` (no other folders).
Set SceneBuilder.Core.Tests.csproj to net8.0, Nullable enable, IsPackable false, bumped
Microsoft.NET.Test.Sdk to 17.9.0, xunit to 2.7.0, xunit.runner.visualstudio to 2.5.7 with
PrivateAssets=all/IncludeAssets, and added a ProjectReference to SceneBuilder.Core. Left the
template's coverlet.collector package untouched (not in blueprint's exclusion list, harmless,
standard xunit template convention). Did not create unity/, Editor asmdef, package.json, or
SceneBuilder.Core.asmdef. .gitignore left unchanged (already adequate per research).

MAPS_TO_BLUEPRINT: All FILES_NEW items from research.md created with the exact specified package
versions/settings. PUBLIC_SURFACE (build+test gate green, three project files exist) satisfied.
No production INTERFACES were specified by the blueprint for this task, and none were added.

DEVIATIONS: none

REUSED: n/a — greenfield task, research confirmed nothing pre-existing to reuse.

## Expected result
TESTS: n/a (SKIPPED per test-writer.md — no test files to turn green)
BUILD: `export PATH="$HOME/.dotnet:$PATH" && dotnet build SceneBuilder.sln` — succeeds, 0
warnings, 0 errors (verified: NU1903 vuln warning does not appear with System.Text.Json 8.0.5).
GATE: `export PATH="$HOME/.dotnet:$PATH" && dotnet test SceneBuilder.sln` — exits 0 ("No test is
available" printed, 0 tests, exit code 0 — verified by direct run, not assumed).
Additionally verified the IsExternalInit shim: a throwaway `record` with an `init` property in
SceneBuilder.Core compiled cleanly (0 errors) on netstandard2.1, then was removed.

STATUS: READY_TO_VALIDATE
