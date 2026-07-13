---
feature: scenebuilder-core-m0-m2
task: b0-t4
agent: tdd-code-writer
updated: 2026-07-13T00:00:00Z
iteration: 1
---

## Implementation
FILES: [SceneBuilder.Core/Serialization/IdentityMapJson.cs]
SUMMARY: Replaced the two `NotImplementedException` stub bodies with delegation to `CanonicalJson.Serialize<IdentityMap>` / `CanonicalJson.Deserialize<IdentityMap>`, mirroring `PlanJson.cs` exactly. `IdentityMap`, `IdentityMapEntry`, and `AssetEntry` POCOs were already fully defined by test-writer per the blueprint (`[JsonPropertyOrder]` fixed key order, default values) and required no changes.
MAPS_TO_BLUEPRINT: `IdentityMapJson.Serialize(IdentityMap) -> string` and `Deserialize(string) -> IdentityMap` both implemented exactly as specified in research.md's INTERFACES section — thin static wrapper over CanonicalJson, no new determinism logic, no polymorphism.
DEVIATIONS: none
REUSED: SceneBuilder.Core/Serialization/CanonicalJson.cs (deterministic serialize/deserialize substrate) — sole serialization path, per research's reuse check.

## Expected result
TESTS: IdentityMap_RoundTripsThroughJson_PreservingEntryOrder, IdentityMap_Serialized_HasSchemaVersionSceneEntriesAssetsKeys_InOrder, IdentityMap_WithNoAssets_SerializesAssetsAsEmptyArray — all pass (verified: `dotnet test SceneBuilder.sln --filter "FullyQualifiedName~IdentityMapJsonTests"` → 3 passed, 0 failed).
BUILD: `export PATH="$HOME/.dotnet:$PATH" && dotnet test SceneBuilder.sln` → 12 passed, 0 failed (9 pre-existing + 3 new), 0 skipped.

STATUS: READY_TO_VALIDATE
