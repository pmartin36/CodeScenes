---
feature: scenebuilder-core-m0-m2
task: b0-t3
agent: tdd-code-writer
updated: 2026-07-13T18:45:00Z
iteration: 1
---

## Implementation
FILES: [SceneBuilder.Core/Serialization/PlanJson.cs]
SUMMARY: Filled in the two `PlanJson` method stubs left by test-writer with thin delegation to
`CanonicalJson.Serialize<Plan>` / `CanonicalJson.Deserialize<Plan>`, per blueprint DATA_FLOW.
Removed the now-unused `using System;` (no longer needed once `NotImplementedException` throws
were replaced). `Plan`, `PlanOp`, `CreateObject` POCOs were already complete as written by
test-writer (per blueprint INTERFACES) — no changes needed there.
MAPS_TO_BLUEPRINT: `PlanJson.Serialize`/`Deserialize` match blueprint's exact one-line bodies
(`CanonicalJson.Serialize(plan)` / `CanonicalJson.Deserialize<Plan.Plan>(json)`). No new
`JsonSerializerOptions`, no converter — CanonicalJson remains the sole determinism substrate.
Unknown-op `JsonException` propagates unwrapped (no try/catch added).
DEVIATIONS: none
REUSED: SceneBuilder.Core/Serialization/CanonicalJson.cs Serialize<T>/Deserialize<T> (camelCase,
WriteIndented, `\r\n`→`\n` normalization); SceneBuilder.Core/Compat/IsExternalInit.cs (already
present, untouched).

## Expected result
TESTS: SceneBuilder.Core.Tests/PlanJsonTests.cs — all 4 cases (Plan_WithSingleCreateObject_
RoundTripsThroughJson, Plan_Serialize_IsByteIdenticalAcrossCalls,
CreateObject_Serialized_ContainsLogicalIdAndName_AndNoExtraFields,
PlanJson_UnknownOp_FailsLoudWithOpNameAndLocation) — confirmed green via
`export PATH="$HOME/.dotnet:$PATH" && dotnet test SceneBuilder.sln --filter "FullyQualifiedName~PlanJsonTests"`
→ `Passed! - Failed: 0, Passed: 4, Skipped: 0, Total: 4`.
BUILD: `dotnet test SceneBuilder.sln` (full suite) → `Passed! - Failed: 0, Passed: 9, Skipped: 0,
Total: 9` — no collateral breakage of pre-existing CanonicalJsonTests.

STATUS: READY_TO_VALIDATE
