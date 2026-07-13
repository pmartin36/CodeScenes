---
feature: scenebuilder-core-m0-m2
task: b0-t2
agent: tdd-code-writer
updated: 2026-07-13T18:23:14Z
iteration: 1
---

## Implementation
FILES:
  - SceneBuilder.Core/Serialization/CanonicalJson.cs (filled in stub bodies)

SUMMARY: Implemented `CanonicalJson` per research's blueprint. `Options` is a cached singleton
`JsonSerializerOptions` built via a private `BuildOptions(converters)` helper with
`PropertyNamingPolicy = JsonNamingPolicy.CamelCase` and `WriteIndented = true` (no explicit
culture/number config needed — STJ number formatting is invariant by design, confirmed in
research). `CreateOptions(converters)` calls the same `BuildOptions` helper to produce a fresh
instance with the canonical settings plus any supplied converters added to `.Converters`, so it
never mutates the shared `Options` singleton. `Serialize<T>` calls
`JsonSerializer.Serialize(value, options ?? Options)` then unconditionally replaces `"\r\n"` with
`"\n"` before returning, so newline normalization is inherited by every current and future caller
rather than being opt-in. `Deserialize<T>` calls `JsonSerializer.Deserialize<T>(json, options ??
Options)`.

MAPS_TO_BLUEPRINT: All four INTERFACES members (`Options`, `CreateOptions`, `Serialize<T>`,
`Deserialize<T>`) implemented exactly as specified. Canonical options configuration matches
research's list (camelCase naming, indented, default null-writing, no explicit culture — STJ is
invariant by default). DATA_FLOW matches: serialize → normalize CRLF→LF → return; deserialize is a
direct passthrough.

DEVIATIONS: none

REUSED: n/a for this task's own logic (greenfield per research's reuse check — no existing
determinism helper in the repo). Reused System.Text.Json (already referenced by
SceneBuilder.Core.csproj per b0-t1).

## Expected result
TESTS: SceneBuilder.Core.Tests.CanonicalJsonTests (all 5 cases) — turned GREEN. Verified via
`export PATH="$HOME/.dotnet:$PATH" && dotnet test SceneBuilder.sln --filter
"FullyQualifiedName~CanonicalJsonTests"` → Passed: 5, Failed: 0, Skipped: 0.
BUILD: `export PATH="$HOME/.dotnet:$PATH" && dotnet test SceneBuilder.sln` (full gate) — exit 0,
Passed: 5, Failed: 0, Skipped: 0 (same 5 tests; no other test files exist yet in the solution).

STATUS: READY_TO_VALIDATE
