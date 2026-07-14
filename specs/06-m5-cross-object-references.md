# M5 — Cross-object references (handles, both directions)

### Additions to the contract
None. M5 introduces no new Core types. It exercises the existing `ValueNode.ObjectRef(targetLogicalId)`
(§3), the `SetReference(path, target)` Plan op (§5), and the `IdentityMap` LogicalId↔GlobalObjectId
mapping (§4). Two refinements of existing semantics (not new types) are pinned below:
- A **null** cross-object reference is `ValueNode.ObjectRef(targetLogicalId = null)` — the canonical
  form of Unity's `{fileID: 0}`.
- Resolution is **two-pass** within a single Materialize so a reference to an object defined later in
  the builder file resolves (forward reference).

## Goal
A serialized field on object A can point to another GameObject or Component **B in the same scene**.
The author expresses this with a named handle — the `var door = scene.Add("Door")` value passed into
another call (§6). Sync runs both directions: code sets the in-scene object reference; a reference
rewired in Unity patches the source to the correct handle (or to null).

## In scope
- A component field of type `UnityEngine.Object` (GameObject / Component / MonoBehaviour) referencing
  another node or component **within the same scene**, carried as `ValueNode.ObjectRef(targetLogicalId)`.
- **Handle authoring:** passing a builder handle (`door`) as the reference target; Core lowers the
  handle to the target's `LogicalId`.
- **Resolution in the adapter:** `LogicalId → GlobalObjectId` (via `IdentityMap`) → live object.
- **Two-pass Materialize:** pass 1 creates/ensures all objects and records their identities; pass 2
  resolves and sets `ObjectRef` fields — so a **forward reference** (target declared later in the
  file, or created in the same run) resolves.
- **Materialize (code→scene):** emit `SetReference(path, target)` ops; adapter sets the field's
  `objectReferenceValue` to the resolved in-scene object.
- **Reconcile (scene→code):** detect a rewired reference — the field's `objectReferenceValue` in the
  snapshot now points at a different in-scene object; map new `GlobalObjectId → LogicalId → handle`
  and patch the source argument to the new handle.
- **Null reference:** field set to None (`{fileID: 0}`) ↔ `ObjectRef(targetLogicalId = null)`; both
  directions.
- **Dangling reference:** a reference whose target `LogicalId`/`GlobalObjectId` no longer exists in
  the scene → **conflict / located error** (§7), surfaced, never silently nulled.

## Out of scope
- References to **assets** (materials, meshes, MonoScripts, prefab assets) — that is **M4**
  (`ValueNode.AssetRef`). M5 is scene-object↔scene-object only.
- Cross-**scene** references (multi/additive scenes) — parked (`needs_research`).
- `UnityEvent` / OnClick persistent-listener wiring (target + method + args) — **M8**. M5 handles the
  plain object-reference field only, not the method binding.
- `[SerializeReference]` managed references — **M9**.
- Prefab-instance references and override round-trip — **M6 / M10**.

## Core deliverables
Types added/changed (all already in §3 — used, not invented):
- `ValueNode.ObjectRef(targetLogicalId: string)` — target by LogicalId; `null` = none.
- Plan op `SetReference(path, target)` (§5), where `target` is the resolved LogicalId (adapter maps
  to the live object).
- `IdentityMap.Entries[]` LogicalId↔GlobalObjectId (§4) — the resolution table.

Functions/behaviors (each a testable contract):
- **Handle lowering:** a handle passed as a reference target lowers to
  `ValueNode.ObjectRef(handle.LogicalId)`. Passing `null` lowers to `ObjectRef(null)`.
- **Two-pass resolution / forward refs:** given a `SceneModel` where A references B and B is declared
  after A, Materialize resolves B's LogicalId to its entry in pass 2 and emits
  `SetReference` for A — no ordering error. A reference to an object created in the same run resolves
  once its identity is recorded.
- **Materialize → Plan:** a field holding `ObjectRef(id)` produces `SetReference(path, id)`; a field
  holding `ObjectRef(null)` produces `SetReference(path, null)` (clears the slot).
- **Diff:** asset-vs-object aside, two `ObjectRef`s are equal iff their `targetLogicalId` matches
  (both null counts as equal). A changed `targetLogicalId` is a change.
- **Reconcile → SourcePatch (rewire):** a snapshot field whose resolved `GlobalObjectId` maps to a
  different `LogicalId` than the parsed source produces a `SourcePatch` swapping the handle argument
  to the new target's handle name.
- **Reconcile → SourcePatch (null):** a snapshot field now `{fileID: 0}` where source had a handle →
  patch the argument to `null` (None). And the reverse (None → a handle).
- **Cross-ref on/to a newly-created object (§13).** A cross-object ref whose source object OR target was
  editor-created in the same edit resolves against M2b's in-memory `AddedEntry` (§13 rule 1): the ref is
  appended onto the just-created statement, and when the target is ALSO new it resolves two-pass (source
  and target appended in pass 1, the handle wired in pass 2). Where single-pass is infeasible, it is
  reported and converges on a guaranteed second Sync (§13 rule 2) — never a silent null. Cites §13
  (create-with-payload).
- **Dangling → conflict:** a snapshot `GlobalObjectId` that maps to no `IdentityMap` entry (target
  deleted), or a source handle whose `LogicalId` has no live object, produces a located conflict
  naming source object, field, and the missing target — never a silent null.

## Editor adapter deliverables
- **Resolve LogicalId → live object** for Materialize: look up `GlobalObjectId` in the `IdentityMap`,
  then `GlobalObjectId.GlobalObjectIdentifierToObjectSlow(...)` → `UnityEngine.Object`; assign to
  `SerializedProperty.objectReferenceValue`; `ApplyModifiedProperties`.
- **Resolve live object → LogicalId** for Reconcile: read the field's `objectReferenceValue`, compute
  its `GlobalObjectId` (`GlobalObjectId.GetGlobalObjectIdSlow(obj)`), reverse-map through the
  `IdentityMap` to a `LogicalId`; hand to Core for the handle patch.
- **Null handling:** `objectReferenceValue == null` ↔ `ObjectRef(null)`.
- **Same-scene guard:** the adapter must confirm the referenced object lives in the same scene; a ref
  to an asset or another scene is reported (asset refs belong to M4).
- **Two-pass execution:** the adapter executes all create/identity ops before `SetReference` ops so
  forward references have live targets by the time they are assigned.

## Authoring API added
Handles are already produced by `scene.Add(...)`; M5 accepts a handle (or `null`) anywhere a
serialized object-reference field is set. A `Component<T>` closure may reference another handle.

```csharp
public class FooScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var door = scene.Add("Door");                       // forward-referenced below
        var opener = scene.Add("Opener");
        opener.Add<Button>(b => b.Set(x => x.target, door)); // handle → ObjectRef(door.LogicalId)

        var idle = scene.Add("Idle");
        idle.Add<Button>(b => b.Set(x => x.target, null));   // None → ObjectRef(null)
    }
}
```
`door` may be declared before or after its use (two-pass resolution). The handle name is the readable
anchor in source; the sidecar carries the GlobalObjectId identity.

## IdentityMap / sidecar changes
- No schema change. M5 **reads** `Entries[]` (LogicalId↔GlobalObjectId) for both resolution
  directions and **relies on** GlobalObjectIds being recorded (post-first-save, per §4).
- A reference whose target has `GlobalObjectId == ""` (target never saved yet) resolves within the
  same Materialize via pass-2 LogicalId resolution; the GlobalObjectId is recorded on save as usual.

## Core test plan
IdentityMap and object resolution are exercised as Core data; the live-object lookup is mocked at the
adapter boundary (Core works in LogicalIds/GlobalObjectIds, never touches `UnityEngine`). RED tests:
1. **Handle lowering:** handle → `ObjectRef(handle.LogicalId)`; `null` → `ObjectRef(null)`.
2. **ObjectRef round-trip:** SceneModel → canonical serialize → parse → equal `ObjectRef`
   (`targetLogicalId` preserved, including null).
3. **Two-pass forward ref:** A references B declared later → Materialize emits
   `SetReference(pathA, B.LogicalId)` with no ordering failure.
4. **Materialize → Plan:** `ObjectRef(id)` → `SetReference(path, id)`; `ObjectRef(null)` →
   `SetReference(path, null)`.
5. **Diff:** equal `targetLogicalId` → no change; changed `targetLogicalId` → change; null↔handle →
   change.
6. **Reconcile rewire → SourcePatch:** snapshot field's GlobalObjectId maps to a different LogicalId
   than source → patch swaps the handle argument to the new handle name.
7. **Reconcile null → SourcePatch:** snapshot field `{fileID:0}` where source had a handle → patch to
   `null`; and None→handle reverse.
8. **Dangling ref → conflict:** GlobalObjectId with no IdentityMap entry (deleted target), or handle
   whose LogicalId has no live object → located conflict naming source object/field/missing target.
9. **Null round-trip:** `ObjectRef(null)` Materialize→Plan→(mock)→snapshot→Reconcile preserves None.
10. **`Reconcile_CrossRefOnNewObject_Converges`** (§13 create-with-payload). A newly editor-created object
    carrying a cross-object ref in one edit → the handle argument is appended onto that object's
    just-created statement (owner mapped via M2b's `AddedEntry`); when the target is also new it resolves
    two-pass; otherwise reported and converged on a second Sync. Second Sync of the unchanged scene is a
    no-op; never a silent null.

## Unity confirmation checklist
1. Author `FooScene` where `Opener`'s `Button.target` is the `Door` handle (Door declared **after**
   Opener); run Materialize. **Expected:** in the Inspector, Opener's Button target slot points at the
   Door GameObject (forward reference resolved).
2. In Unity, drag a **different** GameObject into that Button's target field; trigger Reconcile.
   **Expected:** source updates the handle argument to the new target's handle (e.g.
   `.Set(x => x.target, otherHandle)`).
3. In Unity, set the Button's target field to **None**; trigger Reconcile. **Expected:** source
   reflects `null` (`.Set(x => x.target, null)`).
4. Delete the `Door` GameObject while `Opener` still references it; run sync. **Expected:** a loud,
   located conflict naming `Opener > Button.target` and the missing target — the field is not silently
   cleared.
5. Re-run Materialize after step 2/3. **Expected:** scene matches source, no spurious diff (idempotent
   round-trip).

## Dependencies
- **M2** — Snapshot reader, Reconcile, and Roslyn `SourcePatch` (argument-swap mechanism reused).
- **M3** — components + serialized fields (the field a reference lives on) and generic `.Set`.
- **M1** — Materialize → Plan and the IdentityMap with GlobalObjectIds (the resolution table M5
  reads).

## Risks/notes
- **GlobalObjectId lifecycle:** GlobalObjectIds exist only after first save (§4). A reference authored
  before the target is ever saved must resolve via pass-2 LogicalId resolution, not via a (still
  empty) GlobalObjectId — do not assume the id is populated at resolution time.
- **Rewire vs. delete ambiguity:** distinguish "target changed to another live object" (rewire →
  patch handle) from "target removed / points at nothing valid" (null vs. dangling). A ref to a
  deleted object is a **conflict**, not an implicit null; a ref explicitly set to None is a legitimate
  null.
- **Prefab-instance targets** keyed on `(targetPrefabId, targetObjectId)` (§4) are deferred to M6;
  M5 covers plain in-scene objects.
- **Component vs. GameObject target:** a field may reference a specific Component (e.g. a `Rigidbody`)
  rather than its GameObject; the `LogicalId` must resolve to the component entry, not just its owner,
  so Kind/ComponentType from the IdentityMap disambiguates.
