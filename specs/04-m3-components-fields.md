# M3 — Components + serialized fields (both directions)

### Additions to the contract

M3 introduces **one** concept not literally present in §3, flagged here per the foundation rule:

- **Provisional authored-member field key** — a transient `ComponentData.Fields` key of the form
  `member:<name>` (e.g. `member:mass`) produced by Core's Roslyn parse when a field was authored with
  the *typed* setter `.Set(r => r.mass, v)`. Core cannot resolve a member selector to a serialized
  `propertyPath` because it has no `UnityEngine`. The Editor adapter runs a **`ResolveAuthoredPaths`**
  pass (see Editor deliverables) that rewrites every `member:*` key to its real serialized
  `propertyPath` *before* any `Diff`. Post-resolution, all keys are serialized paths exactly as §3
  requires (`Map<string, ValueNode>` keyed by propertyPath). No new POCO type is added; this is a
  key-space convention plus one adapter behavior. Fields authored with the raw escape hatch
  `.Set("m_Mass", v)` are stored with their path directly and never carry the `member:` sigil.

Everything else uses §3 type names verbatim: `ComponentData`, `TypeRef`, `ValueNode`
(`Primitive`/`Enum`/`Vec2`/`Vec3`/`Vec4`/`Quat`/`Color`/`List`/`Nested`/`Unsupported`), `Plan`,
`ChangeSet`, `SourcePatch`, `SceneSnapshot`, `IdentityMap`.

---

## Goal

Let an author add/remove Components on a GameObject and set their serialized fields as a flat C#
builder, and keep those components and field values in agreement with the live scene in **both**
directions. This is the generic serialization core: primitives, enums, vectors, colors, nested
`[Serializable]` structs, and lists — including `[SerializeField] private` fields addressed by their
serialized path.

## In scope

- Add / remove `ComponentData` on a `GameObjectNode`, **ordered**, **excluding `Transform`** (the
  Transform is owned by M1/M2 and is never added or removed here).
- Setting serialized fields generically through `SerializedObject` / `SerializedProperty` in the
  Editor adapter, dispatched on `SerializedPropertyType`.
- `[SerializeField] private` fields addressed by serialized path (e.g. `_health`). Treated as the
  **normal** case, identical handling to public fields — `SerializedProperty` iteration surfaces
  private serialized members regardless of C# accessibility.
- `ValueNode` coverage for this milestone:
  - `Primitive` — `bool`, `int`, `long`, `float`, `double`, `string`.
  - `Enum` — enums by member name, **including `[Flags]` combinations** (`isFlags:true`, OR-combined members).
  - `Vec2`, `Vec3`, `Vec4`, `Quat`, `Color`.
  - `Nested` — nested `[Serializable]` struct/class (recursive).
  - `List` — `List<T>`/`T[]` of any of the above (order-significant).
  - `Unsupported` — escape hatch for any encountered value M3 does not model; round-trips verbatim
    and is flagged (§7).
- Both directions:
  - **Materialize** (code→scene): lower to `AddComponent`, `RemoveComponent`, `SetField(path,value)`
    ops.
  - **Reconcile** (scene→code): detect component add/remove and field-value changes in the
    `SceneSnapshot` and emit a `SourcePatch`.
- Authoring API: typed setter `.Component<T>(c => c.Set(x => x.field, value))` **and** the generic
  escape hatch `.Set("propertyPath", value)`.
- Type resolution: `TypeRef.FullName` for built-in components (`UnityEngine.*`) and for user
  `MonoBehaviour`s resolved by full type name already present in the project.

## Out of scope

- `AssetRef` fields (materials, meshes, MonoScript identity) — **M4**. Any object-reference-to-asset
  field encountered → `ValueNode.Unsupported`.
- `ObjectRef` cross-object references (handles) — **M5**. Any scene object-reference field
  encountered → `ValueNode.Unsupported`.
- `UnityEvent` / OnClick persistent listeners — **M8** → `Unsupported`.
- `[SerializeReference]` managed-reference polymorphism — **M9** → `Unsupported`.
- Deep script-GUID (`MonoScript`) identity for MonoBehaviours — **M4**. M3 resolves MonoBehaviours by
  full type name only.
- `RectTransform`, prefab instances, transform/name/parent sync (M1/M2/M-later).
- Any `SerializedPropertyType` not in the supported set above (LayerMask, AnimationCurve, Gradient,
  Bounds, Rect, Character, ManagedReference, ObjectReference, …) → `Unsupported`.

## Core deliverables

### Types added/changed (referencing §3)

- **`ComponentData`** (§3, unchanged shape): `LogicalId`, `Type : TypeRef`,
  `Fields : Map<string, ValueNode>`. M3 is the first milestone to populate `Components[]` and
  `Fields`. `Components[]` on `GameObjectNode` is ordered and excludes `Transform`.
- **`TypeRef`** (§3, unchanged): `FullName` (e.g. `"UnityEngine.Rigidbody"`,
  `"Game.Health"`), `AssemblyHint?` (optional for MonoBehaviours; M3 leaves it null and resolves by
  `FullName`).
- **`ValueNode`** (§3): M3 implements construction, canonical serialization, and value-equality for
  `Primitive`, `Enum`, `Vec2/3/4`, `Quat`, `Color`, `Nested`, `List`, `Unsupported`.
- **`Plan` ops** (§5, unchanged): M3 emits `AddComponent`, `RemoveComponent`, `SetField(path,value)`
  where `value` is a `ValueNode`.
- Field-key convention `member:<name>` (see additions note) plus the requirement that Core parse
  produce it for typed setters.

### Functions / behaviors (each a testable contract)

- **Parse (Roslyn → `SceneModel`)**
  - A `.Component<T>(…)` call on a handle produces one `ComponentData` with `Type.FullName` = the
    fully-qualified name of `T`, in source order, appended after any existing components.
  - `.Set("m_Mass", 12f)` → `Fields["m_Mass"] = Primitive(float, 12)`. The string literal is the key
    verbatim.
  - `.Set(r => r.mass, 12f)` → `Fields["member:mass"] = Primitive(float, 12)` (provisional key,
    pending adapter resolution).
  - `.Set("_maxHealth", 100)` → `Fields["_maxHealth"] = Primitive(int, 100)` (private serialized
    field by path; no special-casing vs public).
  - Enum literal `Faction.Enemy` → `Enum("Game.Faction", ["Enemy"], isFlags:false)`. A `[Flags]`
    OR-expression `Layers.Ground | Layers.Water` → `Enum("Game.Layers", ["Ground","Water"], isFlags:true)`.
  - `new Vector3(1,2,3)` / `new Color(1,0,0,1)` / `new Vector2(...)` / `new Vector4(...)` /
    `Quaternion(...)` literals → the matching structured `ValueNode`.
  - A nested initializer for a `[Serializable]` type → `Nested(fields)`; a collection literal →
    `List(items)` preserving order.
  - A value form Core does not recognize → `Unsupported(rawToken)` capturing the verbatim argument
    source text.
- **Materialize → `Plan`** (`Diff(desired, actual)` keyed LogicalId↔GlobalObjectId via IdentityMap):
  - Component present in `desired`, absent in `actual` → `AddComponent(TypeRef)` followed by
    `SetField` ops for its fields, in field order.
  - Component present in `actual`, absent in `desired` → `RemoveComponent`. `Transform` is never in
    the remove set even if it appears in the snapshot.
  - Component present in both, a field value differs → a single `SetField(path, value)` for the
    changed path only.
  - Emitted component ops respect `desired` `Components[]` order.
  - A field whose value is `Unsupported` emits **no** `SetField` (skipped, flagged in the plan
    preview); the live scene value is left untouched.
- **Reconcile → `SourcePatch`** (`Diff(expected, actual)` keyed on GlobalObjectId):
  - A component in the snapshot whose GlobalObjectId is not in the IdentityMap → append a
    `.Component<T>(…)` statement (with raw-path setters for its fields) + a new IdentityMap entry.
  - An IdentityMap component entry whose GlobalObjectId is absent from the snapshot → delete the
    corresponding statement (span-local).
  - A field value that differs between `expected` and `actual` → a **span-local** patch of the value
    argument only. For a field authored with a typed selector, the selector (`r => r.mass`) is left
    untouched and only the literal argument is rewritten. Newly-detected fields are written in raw
    `.Set("m_Path", value)` form.
  - A snapshot field that is `Unsupported` is **not** overwritten in source and the corresponding
    source token is **not** touched (flagged in the preview).
  - Anything not localizable to a single construct → **conflict**, surfaced, never flattened (§5/§7).
- **Canonical serialization** (Core canonical serializer, extended):
  - Each in-scope `ValueNode` kind has a deterministic text form; two structurally equal
    `SceneModel`s serialize byte-identically across runs and processes.
  - `Fields` are emitted in **sorted key order**; `Nested.fields` likewise; `List` preserves index
    order.
  - Floating-point values (`float`/`double`, and vector/quat/color components) are formatted
    round-trip-invariant (shortest round-trippable form, invariant culture).
  - `Enum` serialized as `typeFullName` + members: a single name, or (for `[Flags]`) the members
    joined by `|` in **sorted** order (deterministic); `Unsupported` serialized as its verbatim `rawToken`.
- **Value equality** used by the differ is **exact on canonical form** (no float tolerance), so
  determinism and change-detection agree.

## Editor adapter deliverables

Thin, logic-light Unity-side pieces (confirmed by the checklist, not unit-tested):

1. **`SerializedProperty` dispatch (read + write) layer** — a single dispatch on
   `SerializedProperty.propertyType`:

   | propertyType (+`type`)        | Read → ValueNode                          | Write from ValueNode                     |
   |-------------------------------|-------------------------------------------|------------------------------------------|
   | `Boolean`                     | `Primitive(bool, boolValue)`              | `boolValue`                              |
   | `Integer` (32-bit)            | `Primitive(int, intValue)`                | `intValue`                               |
   | `Integer` (`type=="long"`)    | `Primitive(long, longValue)`              | `longValue`                              |
   | `Float` (32-bit)              | `Primitive(float, floatValue)`            | `floatValue`                             |
   | `Float` (`type=="double"`)    | `Primitive(double, doubleValue)`          | `doubleValue`                            |
   | `String`                      | `Primitive(string, stringValue)`          | `stringValue`                            |
   | `Enum` (non-flags)            | `Enum(FullName, [enumNames[enumValueIndex]], isFlags:false)` | set `enumValueIndex` from the single name |
   | `Enum` (`[Flags]`)            | `Enum(FullName, decompose enumValueFlag→member names, isFlags:true)` | set `enumValueFlag` = OR of the members' bits |
   | `Vector2/3/4`                 | `Vec2/3/4`                                | `vector2/3/4Value`                       |
   | `Quaternion`                  | `Quat`                                     | `quaternionValue`                        |
   | `Color`                       | `Color`                                    | `colorValue`                             |
   | `Generic` (`isArray`)         | `List(items)` via `GetArrayElementAtIndex`| resize + recurse per element             |
   | `Generic` (non-array)         | `Nested(fields)` via child iteration      | recurse into children                    |
   | `ObjectReference`, `ManagedReference`, and all others | `Unsupported(rawToken)` | **no-op** (flagged) |

   Writes call `ApplyModifiedProperties` once per component. Enum `typeFullName` comes from
   reflecting the field's managed type (needed because `SerializedProperty` alone gives only names).
2. **`ResolveAuthoredPaths(SceneModel)`** — rewrites every `member:<name>` field key to its serialized
   `propertyPath` using the component's `SerializedObject`: for user MonoBehaviour serialized fields
   the serialized path equals the member name; for built-ins it maps the C# member to Unity's
   `m_`-mangled path (e.g. `mass → m_Mass`, `useGravity → m_UseGravity`). Runs on the *desired*
   model before `Diff` in both directions. Unresolvable member → located error (§7).
3. **Component add/remove** — `AddComponent` via `GameObject.AddComponent(Type)` (Type resolved from
   `TypeRef.FullName` across loaded assemblies / `TypeCache`); `RemoveComponent` via
   `Object.DestroyImmediate(component)`. Never touches `Transform`.
4. **Read a component's serialized fields into `ValueNode`s** for the snapshot — iterate the
   component's `SerializedObject` visible properties, **skipping** the internal bookkeeping set
   (`m_Script`, `m_ObjectHideFlags`, `m_CorrespondingSourceObject`, `m_PrefabInstance`,
   `m_PrefabAsset`, `m_GameObject`), producing `ComponentData.Fields` keyed by serialized path and
   stamping the component's `GlobalObjectId`.
5. **Type resolution** — resolve `TypeRef.FullName` to a `System.Type`; built-in components from
   `UnityEngine`, MonoBehaviours by full type name (M3 assumption: already compiled in the project).
   *Dependency note:* durable script identity via `MonoScript` GUID (the asset-ref mechanism) is
   deferred to **M4**; M3 name-based resolution is refined there.

## Authoring API added

`.Component<T>(Action<ComponentBuilder<T>> configure)` on a node handle, plus on
`ComponentBuilder<T>`:

- Typed setter: `.Set<TValue>(Expression<Func<T, TValue>> selector, TValue value)` — captures the
  member name; the Editor `ResolveAuthoredPaths` pass maps it to the serialized path.
- Generic escape hatch: `.Set(string propertyPath, object value)` — the string is the serialized
  path verbatim; the **only** way to address a `[SerializeField] private` member and the fallback for
  any built-in path.

Sample (both forms, a private field, an enum, a vector, a color, add + implicit remove-by-absence):

```csharp
public class DemoScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var crate = scene.Add("Crate").Transform(pos: (0, 1, 0));

        // Built-in component — typed setters resolve member -> serialized path (m_Mass, m_UseGravity)
        crate.Component<Rigidbody>(rb => {
            rb.Set(r => r.mass, 12f);
            rb.Set(r => r.useGravity, false);
            rb.Set("m_Drag", 0.5f);              // generic escape hatch: raw serialized path
        });

        // User MonoBehaviour, incl. a [SerializeField] private field addressed by its path
        crate.Component<Health>(h => {
            h.Set("_maxHealth", 100);            // [SerializeField] private int _maxHealth
            h.Set(x => x.regenPerSecond, 2.5f);  // public serialized field, typed
            h.Set(x => x.faction, Faction.Enemy);// enum
            h.Set(x => x.hitLayers, Layers.Ground | Layers.Water); // [Flags] enum combination
            h.Set(x => x.tint, new Color(1, 0, 0, 1));
        });
        // A component previously present in the scene but omitted here is removed on Materialize
        // (Transform is never removed).
    }
}
```

## IdentityMap / sidecar changes

- M3 begins populating **Component-kind entries** in `IdentityMap.Entries[]` (§4 already defines
  `Kind = GameObject | Component`): `LogicalId`, `GlobalObjectId` (`""` until first save),
  `Kind = Component`, `ComponentType` = `TypeRef.FullName`, `ParentLogicalId` = owning
  `GameObjectNode.LogicalId`.
- A component created by Materialize records its own `GlobalObjectId` to the map on save (components
  carry a distinct `GlobalObjectId` from their owner). A component added in Unity and picked up by
  Reconcile gets a new entry with a synthesized `LogicalId` (§4 priority 3).
- No `Assets[]` usage yet (that is M4).
- Sidecar `SchemaVersion` bumps if the entry shape changes; component entries are additive.

## Core test plan (concrete RED tests — behaviors, not impl)

Real `dotnet test` xUnit tests in `SceneBuilder.Core.Tests` (headless, no mocks):

1. **Parse component + fields → model.** Builder with `.Component<Rigidbody>(rb => rb.Set("m_Mass",
   12f))` yields a `ComponentData` with `Type.FullName == "UnityEngine.Rigidbody"` and
   `Fields["m_Mass"] == Primitive(float, 12)`.
2. **Parse typed setter → provisional key.** `.Set(r => r.mass, 12f)` yields
   `Fields["member:mass"] == Primitive(float, 12)`.
3. **Parse private field by path.** `.Set("_maxHealth", 100)` yields
   `Fields["_maxHealth"] == Primitive(int, 100)`.
4. **Parse each ValueNode kind.** bool/int/long/float/double/string, `Enum`, `Vec2/3/4`, `Quat`,
   `Color`, a `Nested` initializer, and a `List` literal each parse to the correct node with correct
   values and (for List) order.
4b. **Parse `[Flags]` enum.** `Layers.Ground | Layers.Water` → `Enum("Game.Layers", ["Ground","Water"],
   isFlags:true)`; canonical form joins members by `|` in sorted order (`Ground|Water`); round-trips.
   Adapter read of `enumValueFlag` decomposes the bitmask to the same member set; write ORs the bits.
5. **Materialize add.** Desired GO has a `Rigidbody` the snapshot lacks → `Plan` contains
   `AddComponent(UnityEngine.Rigidbody)` immediately followed by its `SetField` ops in field order.
6. **Materialize remove.** Snapshot GO has a `BoxCollider` not in desired → `Plan` contains
   `RemoveComponent` for it; a `Transform` present only in the snapshot is **never** in the remove
   set.
7. **Materialize set across kinds.** For primitive/enum/vector/color/nested/list changes, exactly one
   `SetField(path, value)` per changed path with the correct `ValueNode`.
8. **Materialize order.** Components are emitted in desired `Components[]` order; `Transform`
   excluded.
9. **Reconcile changed mass → SourcePatch.** `expected Fields["m_Mass"] == 5`, snapshot `m_Mass == 8`
   → `SourcePatch` rewrites only the value argument (`5f → 8f`); no other span changes.
10. **Reconcile added component → SourcePatch.** Snapshot GO has a component whose `GlobalObjectId`
    is absent from the IdentityMap → patch appends a `.Component<T>(…)` statement and a new component
    IdentityMap entry is produced.
11. **Reconcile removed component → SourcePatch.** IdentityMap component entry whose `GlobalObjectId`
    is absent from the snapshot → patch deletes that statement.
12. **[SerializeField] private addressed correctly.** `Fields["_health"]` survives
    parse → diff → patch with the key `_health` preserved verbatim end-to-end (no `m_`/accessibility
    mangling).
13. **Unsupported round-trips verbatim.** A field authored as an unsupported token parses to
    `Unsupported(raw)`; canonical serialize == the input token; Materialize emits **no** `SetField`
    for it; Reconcile does **not** overwrite it; it is flagged in both previews.
14. **Canonical determinism per kind.** Serializing each `ValueNode` kind twice is byte-identical;
    two structurally equal `SceneModel`s serialize identically; `Fields`/`Nested` keys appear in
    sorted order; floats are round-trip-invariant; `Vec3(1,2,3)` vs `Vec3(1,2,3.0000001)` diff as
    **changed** (exact equality).

## Unity confirmation checklist

1. **Add a component + set a field in Unity.** Add a `Rigidbody` to `Crate`, set `Mass = 7` in the
   Inspector → run Reconcile → the source gains
   `crate.Component<Rigidbody>(rb => rb.Set("m_Mass", 7f))` (raw form for a newly-detected field) and
   the sidecar gains a Component entry with the component's `GlobalObjectId`.
2. **Change an enum and a vector field.** Change `Health.faction` to another value and edit a
   `Vector3` field in the Inspector → Reconcile → the corresponding setters update to the new values;
   no unrelated source lines change.
3. **Private field round-trip.** A custom MonoBehaviour with `[SerializeField] private int
   _maxHealth`, set to `250` in the Inspector → Reconcile → source shows `.Set("_maxHealth", 250)`;
   then Materialize into a fresh scene reproduces `_maxHealth == 250`.
4. **Author a typed setter → scene.** Write `crate.Component<Rigidbody>(rb => rb.Set(r => r.mass,
   3f))` in source → Materialize → the live `Rigidbody.mass == 3`.
5. **Remove a component from source.** Delete a `.Component<T>()` call → Materialize → the component
   is removed from the GameObject; `Transform` is untouched.
6. **Unsupported field is preserved.** With a field of an out-of-scope type present, run a full
   round-trip → the value is unchanged in **both** scene and source and is shown flagged in the
   plan/patch preview.

A milestone-DONE requires: Core tests green in CI **and** every checklist step passing on a real edit
(§8).

## Dependencies

- **M0** — Core/Editor/tests scaffold, sidecar format, Plan round-trip harness.
- **M1** — `SceneModel` GameObject tree + Materialize + IdentityMap with `GlobalObjectId`s.
- **M2** — `SceneSnapshot` reader, `Reconcile`, and Roslyn **span-local** `SourcePatch` (argument
  patch / statement append / statement delete) — M3 reuses all three for field/component edits.

## Risks / notes

- **Member→serialized-path mapping for built-ins** (`mass → m_Mass`) is the highest-risk piece.
  Unity's `m_`-prefix convention is regular but not guaranteed for every built-in; `ResolveAuthoredPaths`
  must fail **loud and located** on any member it cannot resolve rather than guess. The raw
  `.Set("m_Mass", …)` escape hatch is always available as the deterministic fallback and is the form
  Reconcile emits for newly-detected fields.
- **Enum type resolution** needs the field's managed type (via reflection) to fill `Enum.typeFullName`
  and, for `[Flags]`, to decompose `enumValueFlag` into member bits (and OR them back on write);
  `SerializedProperty` exposes only names/indices, so the reflected type is required.
- **`Integer`/`Float` width disambiguation** (int vs long, float vs double) relies on
  `SerializedProperty.type`; the dispatch table pins the exact branch.
- **Nested/List depth** — recursion must be bounded and must not follow object references (those are
  `Unsupported` in M3), avoiding cycles.
- **Bookkeeping-property skip list** is load-bearing: leaking `m_Script` (component identity/type,
  itself an object reference) or prefab bookkeeping into `Fields` would corrupt diffs. Component
  identity/type is handled by type resolution, never as a field.
- **`Unsupported` on the snapshot side** carries a best-effort token; because Materialize skips it and
  Reconcile never overwrites it, an unsupported value can only ever be preserved, never silently
  changed — consistent with §7.
- **Script-GUID identity** is intentionally shallow in M3 (name-based); if two assemblies expose the
  same full type name this is ambiguous — refined in **M4** via `MonoScript` GUID.
