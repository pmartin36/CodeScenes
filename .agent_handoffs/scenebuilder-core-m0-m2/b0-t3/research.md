---
feature: scenebuilder-core-m0-m2
task: b0-t3
agent: tdd-research
updated: 2026-07-13T00:00:00Z
iteration: 1
---

## Mode
ADVERSARIAL (assumptions were provided)

## Verdict on assumptions
VALIDATED

Both assumptions hold exactly, ground-truthed against System.Text.Json 8.0.5 on net8.0 (throwaway probe, output captured):

- "Polymorphic PlanOp keyed on an `op` string discriminator" — satisfied by STJ's built-in
  `[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]` + `[JsonDerivedType(typeof(CreateObject), "CreateObject")]`.
  Serializing `PlanOp op = new CreateObject{...}` (declared as base) emits the discriminator FIRST as `"op": "CreateObject"`.
- "CreateObject serializes exactly {op, logicalId, name}, no transform/component fields" — confirmed. Full Plan shape:
  ```
  {
    "schemaVersion": 1,
    "scenePath": "Assets/Scenes/Demo.unity",
    "ops": [
      { "op": "CreateObject", "logicalId": "Root", "name": "Root" }
    ]
  }
  ```

Two implementation refinements (approach unchanged, but these steer test-writer + code-writer away from real traps):

1. **NO custom `JsonConverter` is needed, and it must NOT be added.** The task DESCRIPTION's "unknown `op` throws a
   fail-loud, located error naming the offending op token (§7)" is ALREADY satisfied by the built-in polymorphism.
   Deserializing an unknown op throws:
   `JsonException: Read unrecognized type discriminator id 'Frobnicate'. Path: $ | LineNumber: 2 | BytePositionInLine: 14.`
   — it names the offending token (`Frobnicate`) AND gives location (Path/LineNumber/BytePositionInLine). A custom
   `JsonConverter<PlanOp>` actually CONFLICTS with `[JsonPolymorphic]` (probe threw
   `NotSupportedException: The converter for derived type 'PlanOp' does not support metadata writes or reads.`), and hand-rolling
   Write loses automatic property emission for no benefit. Use the attributes; keep PlanJson a thin wrapper over CanonicalJson.
2. **`record` value-equality does NOT deep-compare the `PlanOp[] Ops` array** (arrays use reference equality). Probe:
   `back.Equals(plan)` → **False** after round-trip, even though `back.Ops[0].Equals(plan.Ops[0])` → **True** and
   re-serialization is byte-identical. The round-trip test must assert equality element-wise or by re-serialization,
   never `Assert.Equal(plan, roundTripped)` on the whole Plan. (See Test surface.)

## Blueprint
APPROACH: Define `Plan`, abstract `PlanOp`, and `CreateObject` as `record` types with init-only properties and explicit
`[JsonPropertyOrder]`. Put STJ built-in polymorphism attributes on `PlanOp`. `PlanJson` is a `static class` that delegates
straight to the existing `CanonicalJson.Serialize<Plan>` / `CanonicalJson.Deserialize<Plan>` (b0-t2) — which already own
camelCase naming, `WriteIndented`, and `\r\n`→`\n` normalization. No new `JsonSerializerOptions`, no converter. The `op`
discriminator string is literal ("op") and is unaffected by the camelCase naming policy.

INTERFACES (namespace `SceneBuilder.Core.Plan` for POCOs; `SceneBuilder.Core.Serialization` for PlanJson):
```
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(CreateObject), "CreateObject")]
public abstract record PlanOp;

public record CreateObject : PlanOp
{
    [JsonPropertyOrder(0)] public string LogicalId { get; init; } = "";
    [JsonPropertyOrder(1)] public string Name     { get; init; } = "";
}

public record Plan
{
    [JsonPropertyOrder(0)] public int      SchemaVersion { get; init; }
    [JsonPropertyOrder(1)] public string   ScenePath     { get; init; } = "";
    [JsonPropertyOrder(2)] public PlanOp[] Ops           { get; init; } = System.Array.Empty<PlanOp>();
}

public static class PlanJson
{
    public static string Serialize(Plan plan)   => CanonicalJson.Serialize(plan);
    public static Plan   Deserialize(string json) => CanonicalJson.Deserialize<Plan>(json);
}
```
Notes for code-writer:
- Keep `Ops` typed `PlanOp[]` (spec §M0 says `Ops: PlanOp[]`). STJ applies the `[JsonPolymorphic]` on `PlanOp` when the
  array element's declared type is `PlanOp`, so array (de)serialization is polymorphic automatically — verified.
- `abstract record PlanOp;` needs `Compat/IsExternalInit.cs` (already present) for `init` on netstandard2.1; LangVersion is `latest`. No csproj change.
- Do NOT add `[JsonPropertyName("op")]` or any converter; the discriminator is owned by `TypeDiscriminatorPropertyName`.

DATA_FLOW: `Plan` (with `CreateObject` in `Ops`) → `PlanJson.Serialize` → `CanonicalJson.Serialize<Plan>` → STJ writes
schemaVersion/scenePath/ops, each op's discriminator first → `\r\n`→`\n` normalized string. Reverse: JSON string →
`PlanJson.Deserialize` → `CanonicalJson.Deserialize<Plan>` → STJ reads `op` discriminator → constructs `CreateObject`.
Unknown `op` → STJ throws `JsonException` (op token + Path/line/byte) — propagates unwrapped.

FILES_NEW: [SceneBuilder.Core/Plan/Plan.cs, SceneBuilder.Core/Plan/PlanOp.cs, SceneBuilder.Core/Plan/CreateObject.cs, SceneBuilder.Core/Serialization/PlanJson.cs, SceneBuilder.Core.Tests/PlanJsonTests.cs]
FILES_EDIT: [none]  (SceneBuilder.Core/Plan/.gitkeep may be deleted; each new file is ~10-30 lines, far under the 1000-line default budget. No touched file approaches any size limit.)

## Duplicate / reuse check
EXISTING:
- SceneBuilder.Core/Serialization/CanonicalJson.cs:16-20 — `Serialize`/`Deserialize` are the mandated determinism substrate
  (camelCase, WriteIndented, `\r\n`→`\n`). PlanJson MUST call these, not new up its own `JsonSerializerOptions`.
- SceneBuilder.Core/Compat/IsExternalInit.cs:1-6 — enables `init`/records on netstandard2.1; reuse, do not re-add.
- SceneBuilder.Core.Tests/CanonicalJsonTests.cs — the `[JsonPropertyOrder]` + camelCase + LF conventions to mirror in POCOs/tests.
CLEANLINESS:
- Determinism lives in CanonicalJson only (bucket-b0 seam). PlanJson adds ZERO serializer config — it is a thin dispatch layer.
  This same PlanJson is extended in b1-t4 by adding more `[JsonDerivedType]` attributes to `PlanOp` (single source of truth for
  the op registry) — no converter registry to maintain. Keep `PlanOp` the one place ops are registered.
- Property key order is fixed by `[JsonPropertyOrder]` on every POCO property (STJ never alphabetizes; CanonicalJson guarantees no re-sort).
- `System.Text.Json` / `System.Text.Json.Serialization` are the only usings; no new PackageReference.

## Test surface (feed-forward to test-writer)
PUBLIC_SURFACE: `PlanJson.Serialize(Plan)` → canonical JSON string (camelCase keys, 2-space indent, `\n` newlines, key order
schemaVersion→scenePath→ops, each op `op`→logicalId→name); `PlanJson.Deserialize(string)` → `Plan`. `CreateObject`'s only
serialized fields are `op`,`logicalId`,`name`. Unknown `op` discriminator on deserialize → `System.Text.Json.JsonException`
whose `Message` contains the offending op token (the literal unknown string) AND location markers
(`Path`, `LineNumber`, `BytePositionInLine`).

SUGGESTED_TESTS (the four the DELIVERABLE names):
- `Plan_WithSingleCreateObject_RoundTripsThroughJson` — Serialize→Deserialize. Assert equality **element-wise** (e.g.
  `Assert.Equal(plan.SchemaVersion, back.SchemaVersion)`, `Assert.Equal(plan.ScenePath, back.ScenePath)`,
  `Assert.Equal((CreateObject)plan.Ops[0], (CreateObject)back.Ops[0])`) OR by comparing `PlanJson.Serialize(plan) ==
  PlanJson.Serialize(back)`. Do NOT `Assert.Equal(plan, back)` — the `PlanOp[]` makes record equality false (verified).
- `Plan_Serialize_IsByteIdenticalAcrossCalls` — two `PlanJson.Serialize` calls on the same Plan → `Assert.Equal(json1, json2)`.
- `CreateObject_Serialized_ContainsLogicalIdAndName_AndNoExtraFields` — serialize a Plan with one CreateObject; assert JSON
  contains `"op"`,`"logicalId"`,`"name"` and does NOT contain transform/component keys (e.g. `position`/`rotation`/`components`);
  strongest form: the CreateObject object has exactly 3 properties.
- `PlanJson_UnknownOp_FailsLoudWithOpNameAndLocation` — feed JSON whose `ops[0].op` is an unknown token (e.g. `"Frobnicate"`);
  `Assert.Throws<JsonException>`; assert `ex.Message` contains the unknown token AND a location marker such as `"LineNumber"`
  or `"BytePositionInLine"`. (This is STJ 8.0.5's built-in located message; the csproj pins 8.0.5 so it is stable.)

`STATUS: IMPLEMENT`

STATUS: IMPLEMENT
