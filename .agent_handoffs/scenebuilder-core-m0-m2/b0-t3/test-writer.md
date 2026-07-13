---
feature: scenebuilder-core-m0-m2
task: b0-t3
agent: tdd-test-writer
updated: 2026-07-13T18:30:35Z
iteration: 1
---

## Decision
WROTE
REASON: TEST_RECOMMENDATION: write. Round-trip equality, cross-call byte-identity, exact-field
shape, and located unknown-op failure are explicit spec contracts (M0 Core test plan) with real
branching (polymorphic discriminator, fail-loud error path) — worth guarding.

## Tests
FILES:
  - SceneBuilder.Core.Tests/PlanJsonTests.cs (new)
  - SceneBuilder.Core/Plan/PlanOp.cs (new — minimal stub per blueprint INTERFACES: abstract record
    with [JsonPolymorphic]/[JsonDerivedType] attributes only, no method bodies to stub)
  - SceneBuilder.Core/Plan/CreateObject.cs (new — minimal stub per blueprint INTERFACES: record with
    init-only properties only)
  - SceneBuilder.Core/Plan/Plan.cs (new — minimal stub per blueprint INTERFACES: record with
    init-only properties only)
  - SceneBuilder.Core/Serialization/PlanJson.cs (new — signature stub: `Serialize`/`Deserialize`
    both `throw new NotImplementedException()`)

CASES:
  - Plan_WithSingleCreateObject_RoundTripsThroughJson — asserts: serialize→deserialize preserves
    SchemaVersion, ScenePath, and the CreateObject op (compared element-wise per research's
    array-equality warning, never whole-Plan `Assert.Equal`)
  - Plan_Serialize_IsByteIdenticalAcrossCalls — asserts: two `PlanJson.Serialize` calls on the same
    Plan produce identical strings
  - CreateObject_Serialized_ContainsLogicalIdAndName_AndNoExtraFields — asserts: serialized op JSON
    contains op/logicalId/name, excludes position/rotation/components keys, and has exactly 3
    properties on the op object
  - PlanJson_UnknownOp_FailsLoudWithOpNameAndLocation — asserts: deserializing an unknown `op`
    discriminator throws `JsonException` whose Message contains the offending token ("Frobnicate")
    and a location marker ("LineNumber" or "BytePositionInLine")

RUN: export PATH="$HOME/.dotnet:$PATH" && dotnet test SceneBuilder.sln --filter "FullyQualifiedName~PlanJsonTests"
RED_CONFIRMED: yes
RED_OUTPUT:
  Failed: 4, Passed: 0. All four fail on `System.NotImplementedException : The method or operation
  is not implemented.` thrown from `PlanJson.Serialize`/`PlanJson.Deserialize` stub bodies (e.g.
  `PlanJson_UnknownOp_FailsLoudWithOpNameAndLocation`: `Assert.Throws() Failure: Exception type was
  not an exact match. Expected: typeof(System.Text.Json.JsonException). Actual:
  typeof(System.NotImplementedException)`). Full gate run confirms no compile errors and no
  collateral breakage: `Failed: 4, Passed: 5, Skipped: 0, Total: 9` (the 5 passing are the
  pre-existing CanonicalJsonTests).

## Stale tests
PRUNED: none
UPDATED: none
(No existing test in SceneBuilder.Core.Tests references Plan/PlanOp/CreateObject/PlanJson —
verified via grep; this task is additive only.)

## Contract
`PlanJson.Serialize(Plan)` → canonical JSON string via `CanonicalJson.Serialize` (camelCase keys,
2-space indent, `\n` newlines, key order schemaVersion→scenePath→ops, each op `op`→logicalId→name).
`PlanJson.Deserialize(string)` → `Plan` via `CanonicalJson.Deserialize<Plan>`. `PlanOp` is abstract,
polymorphic via STJ's built-in `[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]` +
`[JsonDerivedType(typeof(CreateObject), "CreateObject")]` — no custom JsonConverter. `CreateObject`
serializes exactly `{op, logicalId, name}`. Unknown `op` on deserialize must propagate STJ's
`JsonException` unwrapped (no try/catch swallowing it) so the message carries the offending token
and Path/LineNumber/BytePositionInLine location. Code-writer fills in the two `PlanJson` method
bodies per research's DATA_FLOW (thin delegation to `CanonicalJson.Serialize<Plan>` /
`CanonicalJson.Deserialize<Plan>`); the POCOs (Plan/PlanOp/CreateObject) are already complete as
written — no further code-writer changes needed there unless a deviation is found.

`STATUS: TESTS_RED`
