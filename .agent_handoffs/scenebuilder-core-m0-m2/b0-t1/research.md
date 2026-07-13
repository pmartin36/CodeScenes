---
feature: scenebuilder-core-m0-m2
task: b0-t1
agent: tdd-research
updated: 2026-07-13T18:14:48Z
iteration: 1
---

## Mode
ADVERSARIAL (assumptions were provided)

## Verdict on assumptions
VALIDATED
Both assumptions were checked with a real throwaway solution built by the installed toolchain
(dotnet 8.0.422 SDK, only net8.0 runtime present), not by inference:
- `Microsoft.CodeAnalysis.CSharp` (tested 4.9.2) referenced from a `netstandard2.1` library builds
  and is fully consumable from a `net8.0` xUnit test project via ProjectReference (probe compiled +
  ran a test that called into it). VALIDATED.
- `System.Text.Json` on `netstandard2.1` works via the NuGet package. VALIDATED. One caveat: version
  `8.0.4` raises `NU1903` (known high-severity vuln). The gate `dotnet test SceneBuilder.sln` does NOT
  use `-warnaserror`, so it would not fail — but pin `8.0.5` (verified: builds clean, NU1903 gone).

Two additional risks I surfaced and resolved by real runs (feed-forward, not in the stated assumptions):
1. Empty xUnit project + gate: `dotnet test` on a test project with ZERO test classes exits **0** on
   this SDK/VSTest 17.11.1 (it prints "No test is available" but returns 0). The deliverable's "0 tests
   is acceptable" holds. Verified with a forced clean rebuild.
2. netstandard2.1 + `record`/`init`-setters gap: downstream POCO tasks (b0-t3, b0-t4, b1-t1, b1-t3,
   b2-t1 …) will almost certainly use `record` or `init` accessors. On `netstandard2.1` these fail to
   compile — `error CS0518: Predefined type 'System.Runtime.CompilerServices.IsExternalInit' is not
   defined`. Verified the failure AND the fix. The scaffold must ship the `IsExternalInit` shim once so
   every later task inherits `init`/`record` support by default (per global rule: put the enabler where
   every current+future caller inherits it, not opt-in per task). This is part of THIS blueprint.

## Blueprint
APPROACH: Greenfield .NET scaffold. One solution at repo root tying a `netstandard2.1` Core class
library and a `net8.0` xUnit test project (ProjectReference Tests→Core). Create the four M0 source
folders (Model/, Plan/, Identity/, Serialization/) as empty (with `.gitkeep`) — later tasks fill them.
Ship a single `IsExternalInit` compat shim in Core so downstream POCO tasks compile. Do NOT create
`unity/`, the Editor asmdef, `package.json`, or `SceneBuilder.Core.asmdef` (out of scope per tasks.md).
Do NOT pre-create Parsing/Diff/Materialize/Reconcile folders — those belong to later buckets; M0 layout
names only the four folders above.

INTERFACES: None (no production types in this task). Only project/build configuration + the compat
shim `namespace System.Runtime.CompilerServices { internal static class IsExternalInit {} }`.

DATA_FLOW: n/a (scaffold). Build/test flow: `dotnet test SceneBuilder.sln` → restores both projects →
compiles Core (netstandard2.1) → compiles Tests (net8.0) referencing Core → runs xUnit (0 tests) →
exit 0.

FILES_NEW:
  - SceneBuilder.sln  (repo root; references both projects — create via `dotnet new sln` then
    `dotnet sln add`, do NOT hand-write a stub .sln, an empty/partial file causes MSB5010)
  - SceneBuilder.Core/SceneBuilder.Core.csproj
      * <TargetFramework>netstandard2.1</TargetFramework>
      * <Nullable>enable</Nullable>  <LangVersion>latest</LangVersion>  <ImplicitUsings> may stay off
      * PackageReference Microsoft.CodeAnalysis.CSharp Version="4.9.2"
      * PackageReference System.Text.Json Version="8.0.5"
  - SceneBuilder.Core/Compat/IsExternalInit.cs  (the shim above)
  - SceneBuilder.Core/Model/.gitkeep
  - SceneBuilder.Core/Plan/.gitkeep
  - SceneBuilder.Core/Identity/.gitkeep
  - SceneBuilder.Core/Serialization/.gitkeep
  - SceneBuilder.Core.Tests/SceneBuilder.Core.Tests.csproj
      * <TargetFramework>net8.0</TargetFramework>  <Nullable>enable</Nullable>  <IsPackable>false</IsPackable>
      * PackageReference Microsoft.NET.Test.Sdk Version="17.9.0"
      * PackageReference xunit Version="2.7.0"
      * PackageReference xunit.runner.visualstudio Version="2.5.7"  (PrivateAssets="all" IncludeAssets build/analyzers)
      * ProjectReference → ../SceneBuilder.Core/SceneBuilder.Core.csproj

FILES_EDIT:
  - .gitignore — already present and adequate for .NET (bin/, obj/, *.user, .vs/ all covered).
    No change required; listed in TOUCHES only. Leave as-is.

## Duplicate / reuse check
EXISTING: none — repo currently contains only specs/, .git, .gitignore, .agent_handoffs/. No prior
solution, csproj, or source. Greenfield; nothing to reuse or reinvent.
CLEANLINESS:
  - Follow the exact on-disk layout in specs/01-m0-skeleton.md §"On-disk layout": solution at root,
    Core with Model/Plan/Identity/Serialization/, Tests as sibling. Do not invent extra folders.
  - Generate the .sln with the dotnet CLI (a malformed hand-written .sln yields MSB5010 — observed).
  - Keep the shim `internal` so it never leaks from Core's public surface.
  - File-size budget: every file is a few lines; no file approaches the 1000-line default limit. N/A.

## Test surface (feed-forward to test-writer)
PUBLIC_SURFACE: The observable contract of THIS task is purely the build/gate: from repo root,
`export PATH="$HOME/.dotnet:$PATH" && dotnet build SceneBuilder.sln` succeeds and
`dotnet test SceneBuilder.sln` exits 0 (0 tests acceptable); the three project files
(SceneBuilder.sln, SceneBuilder.Core/SceneBuilder.Core.csproj,
SceneBuilder.Core.Tests/SceneBuilder.Core.Tests.csproj) exist. No production behavior/types.
SUGGESTED_TESTS: none-recommended — pure scaffolding (TEST_RECOMMENDATION: skip). The deliverable IS
the green gate; there is no behavior to assert. First real tests arrive in b0-t2/b0-t3/b0-t4.

STATUS: IMPLEMENT
