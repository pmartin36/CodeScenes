---
feature: scenebuilder-core-m0-m2
task: b0-t4
agent: tdd-test-writer
updated: 2026-07-13T18:36:01Z
iteration: 1
---

## Decision
WROTE
REASON: Real load-bearing JSON contract (exact top-level key order, entry-order preservation, empty-array shape) per TEST_RECOMMENDATION write; matches existing PlanJson test pattern.

## Tests
FILES:
- SceneBuilder.Core.Tests/IdentityMapJsonTests.cs (new)
- SceneBuilder.Core/Identity/IdentityMap.cs (new — full POCO per blueprint, no logic to stub)
- SceneBuilder.Core/Identity/IdentityMapEntry.cs (new — full POCO per blueprint)
- SceneBuilder.Core/Identity/AssetEntry.cs (new — full POCO per blueprint)
- SceneBuilder.Core/Serialization/IdentityMapJson.cs (new — minimal stub, both methods `throw new NotImplementedException()`; code-writer replaces with delegation to CanonicalJson per research.md)

CASES:
  - IdentityMap_RoundTripsThroughJson_PreservingEntryOrder — asserts: Serialize→Deserialize preserves SchemaVersion, Scene, and per-entry field equality in original array order (2 entries, compared entry-by-entry per research.md's array-equality warning).
  - IdentityMap_Serialized_HasSchemaVersionSceneEntriesAssetsKeys_InOrder — asserts: camelCased top-level keys `schemaVersion, scene, entries, assets` all present and appear in that index order in the serialized string.
  - IdentityMap_WithNoAssets_SerializesAssetsAsEmptyArray — asserts: parsed `assets` JSON element is an Array with length 0 when IdentityMap.Assets is empty.

RUN: export PATH="$HOME/.dotnet:$PATH" && dotnet test SceneBuilder.sln --filter "FullyQualifiedName~IdentityMapJsonTests"
RED_CONFIRMED: yes
RED_OUTPUT:
  Failed SceneBuilder.Core.Tests.IdentityMapJsonTests.IdentityMap_RoundTripsThroughJson_PreservingEntryOrder
  System.NotImplementedException : The method or operation is not implemented.
     at SceneBuilder.Core.Serialization.IdentityMapJson.Serialize(IdentityMap map) in .../IdentityMapJson.cs:line 10
  (same NotImplementedException, same call site, for all 3 tests)
  Full-gate run (dotnet test SceneBuilder.sln): 9 passed (pre-existing, unaffected), 3 failed (new, red for the right reason), 0 compile errors.

## Stale tests
PRUNED: none
UPDATED: none
(No existing test references Identity/IdentityMap types; nothing in this task's scope changes prior behavior.)

## Contract
`IdentityMapJson.Serialize(IdentityMap) -> string` / `Deserialize(string) -> IdentityMap`, delegating to `CanonicalJson.Serialize/Deserialize<IdentityMap>` (mirror PlanJson.cs exactly — replace the two `NotImplementedException` stub bodies). `IdentityMap`/`IdentityMapEntry`/`AssetEntry` POCOs are already fully defined per research.md's blueprint (`[JsonPropertyOrder]` on each field, camelCase via CanonicalJson's naming policy) — no further changes needed to those three files. Serialized top-level keys must appear in order `schemaVersion, scene, entries, assets`; `Entries`/`Assets` arrays preserve input order; empty `Assets` serializes as `[]`.

STATUS: TESTS_RED
