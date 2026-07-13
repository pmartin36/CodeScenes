# M1 — Flat hierarchy + transforms, one-way (code→scene)

### Additions to the contract
Named-but-untyped concepts from §2/§5 that M1 gives concrete shape; names are reused verbatim later.
- **`ChangeSet` / `ChangeOp`** — the differ output (§2 `Diff(desired, actual) → ChangeSet`). `ChangeSet`
  is an ordered `ChangeOp[]`; `ChangeOp` variants: `AddNode, RemoveNode, Reparent, Reorder, SetName,
  SetTag, SetLayer, SetActive, SetStatic, SetTransform`.
- **PlanOps implemented (not new — named in §5):** `CreateObject` (from M0), `DestroyObject`,
  `SetParent`, `ReorderChild`, `SetName`, `SetTag`, `SetLayer`, `SetActive`, `SetStatic`, and
  `SetField` used **only** for the three transform property paths (below).
- `SceneModel`, `GameObjectNode`, `TransformData`, `Vec3`, `Quat`, `SceneSnapshot` are used exactly as
  typed in §3 — not additions.

M1 does NOT introduce components, arbitrary field paths, or any new §3 value types. `SetField` in M1 is
constrained to `m_LocalPosition` (`Vec3`), `m_LocalRotation` (`Quat`), `m_LocalScale` (`Vec3`) on the
implicit Transform; general `SetField` / typed setters / other `ValueNode` kinds are M3.

## Goal
Author a flat, nested GameObject hierarchy with names/tags/layers/active/static and transforms in a C#
`ISceneDefinition` builder file; Roslyn-parse it into a `SceneModel`; Materialize it into the live Unity
scene **in place** (reconcile-into-existing, never wipe-and-recreate — §5); and record each object’s
`GlobalObjectId` into the IdentityMap on save. One direction only (code→scene).

## In scope
- Roslyn parse of an `ISceneDefinition` builder file (per §6) → `SceneModel` covering, per
  `GameObjectNode`: `Name`, `Tag`, `Layer`, `Active`, `IsStatic`; `TransformData`
  (`Position`/`Rotation`/`Scale`, `Kind:"Transform"`); ordered `Children`; ordered `Roots`.
- LogicalId derivation per §4 (all three priorities: handle name → `.Id("…")` → synthesized
  `parent/name/siblingIndex`, persisted to the IdentityMap so it stays stable).
- Canonical, deterministic serializer for `SceneModel` (§2) — used by tests and as the equality basis.
- `Diff(desired:SceneModel, actual:SceneSnapshot) → ChangeSet`, keyed LogicalId↔GlobalObjectId via the
  IdentityMap (§5 step 3), producing **in-place** ops against both an empty and a non-empty snapshot.
- `Materialize(SceneModel, SceneSnapshot, IdentityMap) → Plan` lowering the `ChangeSet` to ordered
  `PlanOp`s (§5 step 4).
- Editor adapter executes the `Plan` in place, and on `sceneSaved` records each created object’s real
  `GlobalObjectId` into the IdentityMap.

## Out of scope
- Any scene→code direction: no `Reconcile`, no `SourcePatch`, no `ObjectChangeEvents` (M2).
- Components and serialized fields beyond the three transform paths (M3).
- Asset refs, cross-object refs, prefabs, RectTransform, UnityEvents (M4+).
- Discovering objects the IdentityMap does not know about (the full scene-driven snapshot reader is M2 —
  see the reader-scope note below).

## Core deliverables

### Types added/changed (referencing §3 contract)
- Use `SceneModel`, `GameObjectNode`, `TransformData`, `Vec3`, `Quat` exactly as in §3.
- Populate `SceneSnapshot` (§3) — the actual-state POCO mirroring `SceneModel` shape with each object
  carrying its `GlobalObjectId` — but in M1 only the structural fields M1 owns are populated (identity,
  name/tag/layer/active/static, transform, parent, order); `Components` stay empty until M3.
- New: `ChangeSet` / `ChangeOp` (flagged above).
- Extend `Plan` (M0) with the `PlanOp`s listed above.

### Functions/behaviors (each a testable contract)
- **Parse → model.** Given a builder file that adds `Root` with a child `Child` and a transform, the
  parser produces a `SceneModel` whose `Roots` is `[Root]`, `Root.Children` is `[Child]`, ordered as
  written, with `Name/Tag/Layer/Active/IsStatic` and `TransformData` matching the source.
- **Rotation authored Euler, stored Quat.** Given `.Transform(rot:(0,90,0))`, the `TransformData.Rotation`
  `Quat` equals the quaternion for a 90° yaw; canonical serialization emits the quaternion (§3 note).
- **Defaults applied.** Given a bare `scene.Add("X")`, the node has `Tag="Untagged"`, `Layer=0`,
  `Active=true`, `IsStatic=false`, and an identity `TransformData` (pos 0, identity rot, scale 1).
- **LogicalId from handle.** Given `var player = scene.Add("Player")`, `player`’s `LogicalId` derives
  from the handle name (`"player"` per §4 priority 1), independent of `Name`.
- **LogicalId explicit override.** Given `scene.Add("Enemy").Id("boss")`, `LogicalId=="boss"` (priority 2).
- **LogicalId synthesized + persisted + stable.** Given a handleless `scene.Add("Wall")` at sibling
  index 2 under `Root`, its `LogicalId` synthesizes from `Root/Wall/2` (priority 3) and is written to
  the IdentityMap; a re-parse after inserting a sibling before it keeps the same `LogicalId` (read back
  from the map, not re-derived positionally).
- **Diff vs empty snapshot → all creates.** Given a 2-node `SceneModel` and an empty `SceneSnapshot`,
  `Diff` yields `AddNode` ops (+ transform/name ops) and zero `RemoveNode`.
- **Diff vs matching non-empty snapshot → no-op.** Given a `SceneModel` and a `SceneSnapshot` already
  equal to it (matched via IdentityMap), `Diff` yields an empty `ChangeSet` (idempotent re-materialize).
- **Diff vs drifted non-empty snapshot → in-place edits, never recreate.** Given a snapshot whose `Root`
  has a different position and a renamed child, `Diff` yields `SetTransform` + `SetName` ops on the
  *existing* objects (matched by GlobalObjectId) and **no** `RemoveNode`+`AddNode` pair for them
  (proves reconcile-into-existing, §5).
- **Materialize ordering is legal.** `Materialize` orders the `Plan` so parents are created before
  children and `SetParent`/`ReorderChild` follow creation; lowering a create-heavy `ChangeSet` never
  references a not-yet-created object.
- **Transform lowered to constrained SetField.** A `SetTransform` `ChangeOp` lowers to `SetField` ops
  on paths `m_LocalPosition`/`m_LocalRotation`/`m_LocalScale` only.
- **Fail loud, located (§7).** A builder file with unsupported interleaved logic (a `for` loop
  generating `Add` calls, §6) fails parse with an error naming the source location.

## Editor adapter deliverables
- `PlanExecutor` extended to execute `DestroyObject`, `SetParent`, `ReorderChild`,
  `SetName/Tag/Layer/Active/Static`, and `SetField` for the three transform paths (via `SerializedObject`
  on the Transform), all **in place** — resolving existing targets through the IdentityMap
  `GlobalObjectId → object` (§2 responsibility #4), never destroying+recreating a mapped object (§5).
- An **IdentityMap-driven minimal snapshot read**: for each existing `IdentityMapEntry`, resolve its
  `GlobalObjectId` to the live GameObject and read back the structural fields M1 owns, producing the
  `SceneSnapshot` that `Diff` consumes on re-runs. (Reader scope note: this reads only *known* objects.
  The full scene-driven reader that also discovers *unmapped* user-added objects — the basis for
  sync-back — is M2.)
- On `EditorSceneManager.sceneSaved`, resolve each newly created object’s `GlobalObjectId` and write it
  into its IdentityMap entry, then persist the sidecar.
- The Editor-side fluent builder lowers `UnityEngine.Vector3`/euler authoring to Core POCOs at build
  time (§2) before Core parses — i.e., the parsed source is the C# builder file; the adapter only
  executes the resulting `Plan`.

## Authoring API added
The `ISceneDefinition` / `SceneRoot` fluent surface (Editor assembly; lowers to Core POCOs — §2/§6):
```csharp
public class FooScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var root = scene.Add("Root")
                        .Transform(pos: (0, 1, 0), rot: (0, 90, 0), scale: (1, 1, 1))
                        .Tag("Player").Layer(8).Static();

        root.Add("Weapon").Transform(pos: (0.5f, 0, 0));   // nested child via handle
        root.Add("Muzzle", m => m.Transform(pos: (0, 0, 1))); // nested child via closure

        scene.Add("Ground").Id("ground").Active(false);    // explicit LogicalId + inactive
    }
}
```
- `scene.Add(name)` / `handle.Add(name)` — append a child (ordered); returns a node handle.
- `.Transform(pos?, rot?, scale?)` — rot authored as Euler degrees.
- `.Tag(s)`, `.Layer(i)`, `.Active(bool)`, `.Static()` — GameObject flags.
- `.Id("…")` — explicit LogicalId (§4 priority 2).
- Nested children via handle reuse or a closure `(child => …)`.

## IdentityMap / sidecar changes
- `IdentityMapEntry.GlobalObjectId` is now populated with the real `GlobalObjectId` string on save
  (replacing M0’s `""`).
- Synthesized LogicalIds (§4 priority 3) are persisted at parse time so they survive positional edits.
- `Kind` is `"GameObject"` for all M1 entries (`Component` entries begin in M3). `Assets` still empty.
- `ParentLogicalId` is written for every non-root node.

## Core test plan
`SceneBuilder.Core.Tests` (xUnit, headless — §8). RED tests, behaviors not impl:
- `Parse_RootWithOrderedChildren_ProducesMatchingSceneModel`.
- `Parse_Transform_StoresEulerAuthoredRotationAsQuaternion`.
- `Parse_BareAdd_AppliesContractDefaults` (Untagged/0/true/false/identity transform).
- `LogicalId_FromHandleName_Priority1`.
- `LogicalId_FromExplicitIdCall_Priority2`.
- `LogicalId_Synthesized_IsPersistedAndStableAcrossSiblingInsertion_Priority3`.
- `CanonicalSerializer_SameModel_IsByteIdenticalAcrossCalls`.
- `Diff_ModelVsEmptySnapshot_ProducesOnlyCreates`.
- `Diff_ModelVsEqualSnapshot_ProducesEmptyChangeSet` (idempotent).
- `Diff_ModelVsDriftedSnapshot_ProducesInPlaceEdits_NeverRemoveThenAdd` (reconcile-into-existing).
- `Materialize_OrdersParentsBeforeChildren_AndParentingAfterCreation`.
- `Materialize_LowersTransform_ToConstrainedSetFieldPaths`.
- `Parse_InterleavedControlFlow_FailsLoudWithLocation` (§6/§7).

## Unity confirmation checklist
1. Add a `FooScene : ISceneDefinition` builder file (sample above); trigger the build for it.
   *Expected:* scene gains `Root` with children `Weapon`, `Muzzle` in order, and `Ground`; transforms,
   tag, layer, active, static match the source; scene saves without errors.
2. Inspect `<Scene>.sbmap.json`.
   *Expected:* one entry per node with a **non-empty** `GlobalObjectId`, correct `ParentLogicalId`, and
   the synthesized/explicit LogicalIds.
3. Edit a transform value in the builder file and re-run the build.
   *Expected:* the existing objects move/rotate/scale **in place** — same objects (same
   `GlobalObjectId`, no duplicates), no wipe-and-recreate.
4. Re-run the build with no source changes.
   *Expected:* no-op — no new objects, no diffs applied (idempotent).
5. Reorder two siblings in the builder file and re-run.
   *Expected:* sibling order in the Hierarchy updates via reorder, objects keep their `GlobalObjectId`s.

## Dependencies
- **M0** — Plan container/JSON, IdentityMap sidecar, `PlanExecutor`+menu harness, on-disk layout.

## Risks/notes
- Reader scope is deliberately narrowed to IdentityMap-known objects; do NOT build the scene-driven
  discovery reader here — it belongs to M2 where unmapped objects signify user edits.
- Synthesized-LogicalId ambiguity (two same-named siblings later reordered) is only *detected* as a
  conflict in M2’s Reconcile (§4); M1 persists the synthesized id and moves on.
- Euler↔quaternion: author Euler, store/serialize quaternion (§3 note); tests pin the conversion so M2
  write-back can emit Euler deterministically.
- Keep the adapter logic-light (§2): matching, `SerializedObject` writes, and id capture only; all
  diff/ordering decisions stay in Core.
- **Sample seed (§12):** the `FooScene` example authored for M1's confirmation (code→scene build) is
  the code→scene half of the shipped `Samples~/RoundTripDemo`; it is authored in the test project's
  `Assets` now and promoted verbatim into the package sample once the round-trip (through M2) is proven.
