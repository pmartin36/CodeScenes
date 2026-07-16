# M-Nested — nested serializable value types (author `[Serializable]` structs/classes from code)

### Additions to the contract

**One field on one `ValueNode` case; no new kind, no new op.** This milestone adds
`TypeName : string` to the §3 `ValueNode.Nested` case so the node carries the concrete C# type it
represents. It reuses the entire existing nested machinery (`ValueNode.Nested`, `FieldMap`, the
`SetField` Plan op, `SerializedFieldBridge.ReadNested`/`WriteProperty`, `ValueNodeParser.ParseNested`,
`SourceExpr.ValueNodeLiteral`). It introduces **no** new `ValueNode` kind, **no** new `PlanOp`, and
**no** sidecar change.

Why a field rather than a new kind: a nested value already *is* a `ValueNode.Nested` — the read path,
the write path, diff, canonical serialization and the parser all handle it end-to-end today. The one
thing the node fails to carry is the **name of the type being constructed**, which is exactly what the
emitter needs and exactly what it currently fakes as `object`. This is a missing datum on an existing
type, not a parallel hierarchy.

| Added | Shape (summary) | Owner |
|---|---|---|
| `ValueNode.Nested.TypeName : string` | fully-qualified C# type name of the nested value; **amends §3** `Nested(fields)` → `Nested(typeName, fields)` | M-Nested |

The §11 ledger row `FieldMap … backs ComponentData.Fields + ValueNode.Nested.Fields (M1)` is
unchanged — `FieldMap` still holds the fields; only the owning `Nested` record gains a sibling
property.

---

## Goal

A component field whose value is a user `[Serializable]` **struct** or **class** (e.g.
`struct Damage { public float amount; }` on a MonoBehaviour) round-trips through code in both
directions and the emitted source **compiles**. Today the scene→code path emits the field as the
literal `new object { amount = 5f }`, which is **CS0117 — uncompilable** (`object` has no member
`amount`). After this milestone it emits `new Damage { amount = 5f }` (or the fully-qualified form for
a namespaced type), which compiles and parses back to an equal model.

## The bug (observed in the shipped code, not theorized)

`SourceExpr.ValueNodeLiteral` renders every `ValueNode.Nested` as the fixed placeholder string
(`SceneBuilder.Core/Reconcile/SourceExpr.cs:97-99`):

```csharp
ValueNode.Nested nested => "new object { " +
    string.Join(", ", nested.Fields.Select(kv => kv.Key + " = " + ValueNodeLiteral(kv.Value))) +
    " }",
```

`new object { amount = 5f }` does not compile — `object` has no member `amount` (CS0117). The node is
**reachable from a real scene read**, so this is not dead code:

- `SerializedFieldBridge.ReadProperty` routes `SerializedPropertyType.Generic` (non-array) to
  `ReadNested` (`SerializedFieldBridge.cs:234-235`), which returns a populated `ValueNode.Nested`
  (`:290-307`).
- The write side already handles `Nested` (`SerializedFieldBridge.cs:360-370`, walks `Fields` by
  relative path) and Materialize already carries a `Nested` value verbatim through a single `SetField`
  op (`Materializer.EmitFieldOp:167-170`) — so **code→scene works today**; only the emitted **source
  text** is broken.

**Trigger:** an ordinary MonoBehaviour with a `[Serializable]` struct/class field, its value edited in
the Inspector, then Sync → the emitted builder source contains `new object { … }` and will not
compile. This directly violates CLAUDE.md's "Generated C# must compile," which the gate's Roslyn
compile assertion (`unity-gate/Assets/GateTests/EmittedCodeCompiles.cs`) enforces. **No test covers
the emit path today** — `grep 'new object {'` over `SceneBuilder.Core.Tests/` and
`unity-gate/Assets/GateTests/` is empty.

## Root cause (verified against the code)

`ValueNode.Nested(FieldMap Fields)` (`SceneBuilder.Core/Model/ValueNode.cs:70`) **carries no type
name**. On the parse side, `ValueNodeParser.ParseObjectCreation` calls
`ParseNested(objectCreation.Initializer!, whole)` and **discards `objectCreation.Type`**
(`ValueNodeParser.cs:158`, `ParseNested:183-198`). So by the time a `Nested` reaches the emitter there
is no concrete type left to render — hence the `object` placeholder. On the read side,
`ReadNested` never records the type either. The fix carries the type through the model from both
producers (parser and adapter) to the one consumer (emitter).

This is the same missing-type defect **M9** hits from the parser side
(`specs/completed/10-m9-serializereference.md` — `[SerializeReference]` polymorphic values also lose
`objectCreation.Type`); giving `Nested` a `TypeName` de-risks M9, which needs a by-value node to carry
its concrete type before the polymorphic `ManagedReference` node can be layered on. **M9 is not in
scope here** (see Out of scope).

## Research — verified against the real editor (Unity 6000.5.3f1)

The emit side is mechanical once the type is in the model. The load-bearing risk was the **read
side**: when the adapter reads a nested serialized field, can it recover a concrete type name it can
put into the model and later emit as `new <Type> { … }` that Roslyn resolves? This was settled by
running the installed editor in batchmode against live `SerializedObject`s, not from documentation.

**Probe fixture** — a MonoBehaviour with one field per case; for each, `SerializedProperty.propertyType`
and the exact `SerializedProperty.type` string were recorded (verbatim editor output):

| Case (field) | Managed type | `propertyType` | `SerializedProperty.type` (verbatim) |
|---|---|---|---|
| `[Serializable] struct` | `struct Damage { float amount; int kind; }` | `Generic` | `"Damage"` |
| `[Serializable] class` | `class Loadout { string name; int slots; }` | `Generic` | `"Loadout"` |
| nested struct-in-struct (outer) | `struct Outer { Inner inner; float y; }` | `Generic` | `"Outer"` |
| nested struct-in-struct (child) | `Inner inner` | `Generic` | `"Inner"` |
| struct in `List<>` (the list prop) | `List<Damage>` | `Generic` (`isArray=True`) | `"Damage"` |
| struct in `List<>` (element `[0]`) | element | `Generic` | `"Damage"` (`arrayElementType="Damage"`) |
| struct in `[]` array (element `[0]`) | element | `Generic` | `"Damage"` |
| **generic** `Pair<int>` | `struct Pair<T> { T value; }` | `Generic` | **`"Pair`1"`** |
| baseline `Vector3` | — | `Vector3` (not `Generic`) | `"Vector3"` |

**Namespaced type — second probe** (a `[Serializable] struct MyGame.Combat.NsDamage`):

| Source | Value (verbatim) |
|---|---|
| `SerializedProperty.type` | `"NsDamage"` |
| reflection `FieldType.Name` | `"NsDamage"` |
| reflection `FieldType.FullName` | `"MyGame.Combat.NsDamage"` |

**What the probes prove:**

1. For ordinary `[Serializable]` structs and classes — including **nested** structs and struct
   **elements of `List<>`/arrays** — `SerializedProperty.type` returns the **simple, unmangled C#
   type name** (`Damage`, `Loadout`, `Inner`), which reads directly into `new <Name> { … }`.
2. **`SerializedProperty.type` drops the namespace.** A `MyGame.Combat.NsDamage` field reads as bare
   `"NsDamage"`. Emitting `new NsDamage { … }` compiles **only if that namespace is already in
   scope** in the builder file — which is not guaranteed. So `.type` is **not** a safe emit source
   for a namespaced type. (This corrects the initial plan to reuse `ValueNodeParser.TypeNameOf`,
   which likewise returns only the simple name — see "did not survive verification" in the report.)
3. **`SerializedProperty.type` mangles generics** — a `Pair<int>` reads as `` "Pair`1" ``, a CLR
   arity-mangled string that is **not** a legal C# type expression and cannot be emitted.
4. **Reflection recovers the fully-qualified, resolvable name.** `ResolveFieldType` (the reflection
   walker already shipped at `SerializedFieldBridge.cs:450`, already used by `ReadEnum`) returns the
   leaf field's `System.Type`, whose `FullName` is `MyGame.Combat.NsDamage` — globally resolvable, no
   `using` required. `ResolveFieldType` **already unwraps** array/`List<>` element types
   (`:479-487`), so it returns the element's type for a collection element's `propertyPath`
   (`foo.Array.data[0]` → normalized → element type).

**Decision the probes drive:** the adapter recovers the nested type **via reflection FullName, not
`SerializedProperty.type`.** `FullName` is namespace-qualified (always compiles) and it is where the
generic/native edge cases surface as a guardable signal (a backtick, or a null return). The design is
therefore:

- **Read** nested type = `ResolveFieldType(target, propertyPath)?.FullName`, with `+` (CLR nested-type
  separator) normalized to `.` so a C#-nested declared type emits as `Owner.Inner`.
- **Guard** — if reflection returns `null` (native/unresolvable field), or the type is generic
  (`Type.IsGenericType`, equivalently `FullName` contains `` ` ``), the adapter returns
  `ValueNode.Unsupported` (verbatim round-trip, flagged) **instead of** a `Nested` — never emit a name
  that will not compile. This is the loud-vs-quiet-correct call: a preserved-verbatim `Unsupported`
  beats a compiling-looking-but-broken `Nested`.

**Round-trip stability.** The persisted form is the **fully-qualified** type name; the parser captures
the full written type expression (below), so `new MyGame.Combat.Damage { … }` is byte-stable across
syncs. A human who authors the short form (`new NsDamage { … }` under a `using`) is **normalized once**
to the FQN on the first Sync, then stable — the same one-time normalization the project already
applies to float suffixes.

**Version stability.** No fileId, GUID, or hardcoded type table is involved; the type name is derived
live from the running editor (reflection) on every read and re-derived from source text (Roslyn) on
every parse. Both sides of a Diff are derived within one session and always agree.

## In scope

- **A component field whose value is a user `[Serializable]` struct or class**, both directions:
  code→scene (already works — asserted, not re-implemented) and scene→code (the emit fix).
- **Round-trip byte-stability** for the FQN form, and **the emitted source compiles** (the gate's
  Roslyn assertion covers it).
- **Nested-in-nested** — a serializable struct field inside another serializable struct
  (`Outer.inner`), recursively.
- **Nested inside a `List<>`/array** — `List<Damage>` / `Damage[]`: each element emits its own
  `new Damage { … }`; the list emits `new[] { new Damage { … }, … }`, which infers the element type
  and compiles. Free mixing with other list element kinds is out (a serialized field is homogeneous).
- **Namespaced serializable types** — emitted fully-qualified so no `using` is required.
- **`TypeName` participates in equality and canonical JSON** so two `Nested` values of different types
  with identical fields are not equal, and the type survives the sidecar/model round-trip.

## Out of scope

- **`[SerializeReference]` polymorphic managed references** — that is **M9**
  (`specs/completed/10-m9-serializereference.md`): `ValueNode.ManagedReference` + the
  `SetManagedReference` op, where the *runtime* type of the value can differ from the *declared* field
  type (polymorphism, `SerializeReference`). This milestone is **plain by-value nested serialization**:
  the value's type **is** the field's declared type, recovered by reflection on the owning component.
  The boundary is explicit — M9 owns polymorphism and the reference-type-name metadata
  (`managedReferenceFieldTypename`/`managedReferenceFullTypename`); M-Nested owns the by-value
  `new <Type> { … }` literal. M-Nested's `TypeName` field is a prerequisite M9 reuses, not a merge.
- **Generic serializable types** (`Pair<T>`) — `SerializedProperty.type` mangles them (`` Pair`1 ``,
  §Research) and reflection FullName carries backtick/arity noise; they route to `ValueNode.Unsupported`
  (preserved verbatim, flagged), never a broken `new Pair`1 { … }`.
- **Authoring a nested value with anything but an object-initializer** — `new Damage(5f)` (constructor
  args, no `{ … }`) already falls to `Unsupported` in `ParseObjectCreation` and stays there.
- **Unsupported leaf fields inside a nested value** round-trip as `Unsupported` per the existing
  per-field rules; this spec does not change leaf handling, only the container's type name.

## Core deliverables

Types added/changed:
- **`ValueNode.Nested.TypeName : string`** (`SceneBuilder.Core/Model/ValueNode.cs:70`) — the
  fully-qualified C# type name (e.g. `"Damage"`, `"MyGame.Combat.NsDamage"`). The record becomes
  `Nested(string TypeName, FieldMap Fields)`. **Default record equality already includes it** (ordinal
  string compare) alongside the existing `FieldMap` value-equality — no custom `Equals` needed. Every
  construction site updates: `ValueNodeParser.ParseNested:197`, `SerializedFieldBridge.ReadNested:306`,
  and any test fixtures/`Nested(...)` literals. **Invariant:** a `Nested` reaching the emitter always
  has a non-empty, C#-resolvable `TypeName`; producers that cannot supply one emit `Unsupported`
  instead (read side) — the parser always fills it from source text.

Functions/behaviors (each a testable contract):

- **Parse — capture the concrete type.** `ValueNodeParser.ParseObjectCreation` passes the type through
  to `ParseNested`, which records **the full written type expression** —
  `objectCreation.Type.ToString()` trivia-trimmed (e.g. `"MyGame.Combat.Damage"`, `"Damage"`), **not**
  `TypeNameOf` (which drops the namespace — §Research). Result:
  `new Damage { amount = 5f }` → `Nested { TypeName = "Damage", Fields = { amount: 5f } }`. A malformed
  initializer element (non-`ident = value`) still returns `Unsupported` verbatim (parser stays total,
  never throws).
- **Emit — render the real type.** `SourceExpr.ValueNodeLiteral`'s `Nested` arm becomes
  `"new " + nested.TypeName + " { " + <fields> + " }"`. Fields render via the existing recursive
  `ValueNodeLiteral`. `new object { … }` is **deleted**. (A `Nested` with empty `TypeName` is an
  invariant violation; emit may treat it as a defect — it never occurs from the shipped producers once
  the parser and adapter are fixed.)
- **Canonical JSON round-trip.** `TypeName` serializes as a camelCase `"typeName"` string property
  alongside the existing `"fields"` object (which continues through `FieldMapJsonConverter`). No new
  converter; System.Text.Json handles the added string property. Determinism holds (property order is
  stable by declaration).
- **Equality keyed on type.** Two `Nested` with equal `Fields` but different `TypeName` are **not**
  equal (default record equality). This is what makes Diff report a type change and prevents a spurious
  no-op when a field's serialized type changes.
- **List of `Nested`.** `ValueNode.List` already emits `new[] { … }` over `ValueNodeLiteral` per item;
  with the fix each item is `new Damage { … }` and `new[] { new Damage { … }, new Damage { … } }`
  compiles (element type inferred). Parse of that array yields a `List` of typed `Nested`. No `List`
  code change — asserted behavior.
- **Interaction with the unresolved-AssetRef skip (§17):** a `Nested` value is materialized as a
  single `SetField` op (`Materializer.EmitFieldOp:167-170`) and is **not** touched by the
  AssetRef-unresolved skip path (`:126-162`), which only fires for `ValueNode.AssetRef` and lists
  thereof. Unchanged — asserted, not modified.

## Editor adapter deliverables

All in `com.scenebuilder/Editor/SerializedFieldBridge.cs`.

- **`ReadNested` — recover and stamp the concrete type** (`:290-307`). Resolve the type via
  `ResolveFieldType(p.serializedObject.targetObject, p.propertyPath)` (already present, already
  element-aware). Compute the emit name = `type.FullName` with `'+'`→`'.'`. Return
  `new ValueNode.Nested(typeName, new FieldMap(fields))`.
  - **Guard:** if `ResolveFieldType` returns `null`, **or** `type.IsGenericType` (equivalently
    `FullName` contains `` '`' ``), return `ValueNode.Unsupported(p.type)` (verbatim, flagged) **instead
    of** a `Nested`. Never produce a `Nested` whose `TypeName` will not compile.
- **`ReadProperty` routing** (`:234-235`) is unchanged: `Generic` non-array → `ReadNested`,
  `Generic` array → `ReadList` (which recurses into elements, each re-entering `ReadNested` with the
  element's `propertyPath` — reflection unwraps the element type).
- **`WriteProperty`'s `Nested` arm** (`:360-370`) — **verify unchanged.** It walks `Fields` by
  `FindPropertyRelative` and never reads a type name; the write side needs no `TypeName`. Note it in
  the deliverable so the pipeline confirms rather than assumes.

## Authoring API

**No new authoring surface.** A user authors a nested value with a plain C# object initializer of
their own serializable type:

```csharp
using static SceneBuilder.Authoring.SceneBuilder; // existing builder entry

public class FooScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var enemy = scene.Add("Enemy");
        enemy.Component<Weapon>(c => c.Set("damage", new Damage { amount = 5f, kind = 1 }));

        // Nested-in-nested and lists compose naturally:
        enemy.Component<Weapon>(c => c.Set("volley", new[] {
            new Damage { amount = 5f, kind = 1 },
            new Damage { amount = 2f, kind = 0 },
        }));
    }
}
```

The user's serializable type (`Damage`) must be visible to the builder project — it references the
user's game assembly the same way it references the Unity managed DLLs (the builder lives outside
`Assets/` with its own `.csproj`). SceneBuilder never executes the builder; it parses the source text,
so the parser only needs the text to be well-formed, and the emitter re-emits the same type name.

## IdentityMap / sidecar changes

**None.** A nested value is an inline field value; it has no identity and no asset GUID. The sidecar is
untouched.

## Core test plan

New file `SceneBuilder.Core.Tests/NestedValueTypeTests.cs` (plus noted extensions to existing files),
xUnit, style `Subject_Condition_ExpectedOutcome`.

**The RED regression (fails TODAY):**
1. `SourceExpr_NestedStruct_EmitsTypedInitializerNotObject` — a `Nested { TypeName = "Damage" }` →
   emits `new Damage { amount = 5f }`; the output **contains no `new object`**. This is the test the
   shipped `new object { … }` fails.
2. `Parse_EmitNestedStruct_TextRoundTripsIdentically` — `new Damage { amount = 5f, kind = 1 }` →
   parse → emit → identical source text; and the round-trip **contains no `new object`**.

**Parse:**
3. `Parse_NestedStruct_CapturesTypeName` — `new Damage { amount = 5f }` →
   `Nested { TypeName == "Damage" }`, `Fields` = `{ amount: 5f }`.
4. `Parse_NamespacedNestedStruct_CapturesFullyQualifiedTypeName` —
   `new MyGame.Combat.Damage { amount = 5f }` → `TypeName == "MyGame.Combat.Damage"` (the namespace is
   **not** dropped — the `TypeNameOf`-insufficiency pin).
5. `Parse_NestedInNested_CapturesBothTypeNames` —
   `new Outer { inner = new Inner { x = 1f }, y = 2f }` → outer `TypeName == "Outer"`, the `inner`
   field is a `Nested { TypeName == "Inner" }`.
6. `Parse_NestedWithNonInitializerElement_YieldsUnsupported` — a malformed initializer element still
   falls to `Unsupported`, verbatim, no throw (parser stays total).

**Emit:**
7. `SourceExpr_NamespacedNestedStruct_EmitsFullyQualifiedType` → `new MyGame.Combat.Damage { … }`.
8. `SourceExpr_ListOfNested_EmitsTypedArrayThatCompilesShape` — a `List` of two `Nested{Damage}` →
   `new[] { new Damage { … }, new Damage { … } }` (element type inferred; no `new object`).

**Value/identity:**
9. `Nested_EqualityKeyedOnTypeNameAndFields` — same `Fields`, different `TypeName` → **not** equal;
   same `TypeName` + same `Fields` → equal.
10. `Nested_CanonicalRoundTrips` — SceneModel → canonical JSON → deserialize → equal `Nested`,
    `TypeName` preserved; `"typeName"` present in the JSON.

**List / nesting round-trips:**
11. `Parse_EmitNestedInList_TextRoundTripsIdentically`.
12. `Parse_EmitNestedInNested_TextRoundTripsIdentically`.

## Unity confirmation checklist → EditMode tests

These become EditMode round-trips in `unity-gate/Assets/GateTests/` per CLAUDE.md — a new
`RoundTripNestedValueTests.cs` in the established `Direction_Scenario_Expectation` style, driving
`SceneBuilderBuild.Run` / `EmittedCodeCompiles.SyncAndAssertCompiles` against a live scene.

**Fixture deliverable (call it out):** the existing `GateSampleBehaviour`
(`unity-gate/Assets/GateTests/…/GateSampleBehaviour.cs`) has only `public int Health;` — **no
nested-struct field.** A serializable-struct field (e.g. `public Damage Damage;` with
`[Serializable] struct Damage { public float amount; public int kind; }`) must be **added** to a gate
fixture MonoBehaviour so the round-trip has a real nested value to exercise. Because
`BuilderCompileCheck.References()` references every loaded editor assembly
(`BuilderCompileCheck.cs:81-111`), the fixture's compiled assembly is on the compile classpath, so an
emitted `new GateFixtures.Damage { … }` resolves — this is why the emit must be namespace-qualified.

1. **Author a nested value from code (headline).** Build a scene assigning
   `new Damage { amount = 5f, kind = 1 }` to the fixture field. **Expected:** the live component's
   struct reads back `amount == 5`, `kind == 1` (assert the actual serialized values, not a label).
2. **Scene → code emits a compilable typed initializer (the bug fix).** Set the struct's fields in a
   live scene; Sync via `SyncAndAssertCompiles`. **Expected:** the emitted source contains
   `new GateFixtures.Damage { … }` (fully qualified), contains **no `new object`**, and the compile
   assertion passes.
3. **Round-trip is a no-op.** Sync the same unchanged scene twice. **Expected:** the second Sync
   produces no patch — the FQN form is byte-stable.
4. **Nested-in-nested.** A fixture field of a struct that itself contains a serializable struct →
   Build → Sync. **Expected:** emits `new Outer { inner = new Inner { … }, … }`, compiles, no-op on
   resync.
5. **List of nested.** A `Damage[]`/`List<Damage>` fixture field → Build two elements → Sync.
   **Expected:** emits `new[] { new GateFixtures.Damage { … }, … }`, compiles, round-trips.
6. **Generic/native field stays verbatim, never breaks compile.** A field the adapter cannot resolve
   to a concrete non-generic type reads as `Unsupported` (flagged, `Plan.Skipped`), and Sync still
   emits compiling source (no `new Pair`1`).

## Dependencies

- **M3** (`specs/completed/04-m3-components-fields.md`) — components + serialized fields, `.Set`, the
  `SerializedFieldBridge` read/write of `Generic` properties this milestone extends.
- **M1** — `FieldMap`, which continues to back `Nested.Fields`.
- **M2** — Reconcile + Roslyn `SourcePatch` argument rewriting (the emit path being fixed).
- The gate's Roslyn compile assertion (`unity-gate/Assets/GateTests/EmittedCodeCompiles.cs`,
  `com.scenebuilder/Editor/BuilderCompileCheck.cs`) — the enforcement surface for "emitted C# compiles."

## Risks / notes

- **The type-recovery mechanism is the load-bearing choice, and it is settled by probe, not doc.**
  `SerializedProperty.type` looks like the obvious source but it **drops the namespace** and **mangles
  generics** (§Research). The adapter must use reflection (`ResolveFieldType`) `FullName`. Using
  `.type` (or `ValueNodeParser.TypeNameOf`, which is simple-name-only) for emit would silently produce
  source that fails to compile for any namespaced type — the exact class of bug this spec exists to
  kill.
- **Emit fully-qualified, always.** The builder file carries no guaranteed `using` for the user's
  namespaces, so a bare `new NsDamage { … }` is a compile gamble. Fully-qualified `new
  MyGame.Combat.NsDamage { … }` always resolves. A human who authored the short form is normalized once
  to the FQN, then stable.
- **Guard generics/native at the read chokepoint.** The one place a non-emittable type can enter the
  model is `ReadNested`; the generic/`null` guard lives there so **every** caller inherits it and no
  future read path can leak a `new Pair`1 { … }`. Per CLAUDE.md, the safety is default-on, not
  opt-in.
- **This de-risks M9 but is not M9.** `TypeName` is exactly the datum M9's polymorphic
  `ManagedReference` also needs. Keeping them separate nodes (by-value `Nested` vs polymorphic
  `ManagedReference`) preserves the M9 boundary; do not fold `[SerializeReference]` handling in here.
- **Constructor-signature ripple.** Making `Nested` a two-arg record touches every `new
  ValueNode.Nested(...)` in Core, the adapter, and tests. It is a compile-forced sweep (the build
  fails until each is updated), so nothing can silently keep the old shape.
