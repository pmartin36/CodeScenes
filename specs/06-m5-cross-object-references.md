# M5 — Cross-object references (handles, both directions)

### Additions to the contract

**M5 introduces new Core surface — it does NOT already exist** (grep-verified 2026-07: no `ObjectRef`,
no `SetReference`, no `DanglingReference` anywhere in the tree). This spec previously claimed "no new
Core types"; that was wrong. M5 adds a scene-object reference value kind plus the machinery to author,
plan, diff, and reconcile it, **mirroring the existing asset-ref path one-for-one** (`ValueNode.AssetRef`
/ `PlanOp.SetAssetRef` / `ConflictKind`):

| Added | Shape | Mirrors |
|---|---|---|
| `ValueNode.ObjectRef(string? TargetLogicalId)` — new record + `[JsonDerivedType]` registration | a serialized field pointing at another **scene object/component** by `LogicalId`; `null` = Unity `{fileID: 0}` (None) | `ValueNode.AssetRef` |
| `PlanOp.SetReference(string Path, string? TargetLogicalId)` — new | set/clear an object-reference field; the adapter maps `TargetLogicalId → GlobalObjectId → live object` | `PlanOp.SetAssetRef` |
| `ConflictKind.DanglingReference` — new enum member | a reference whose target no longer exists → located conflict, never a silent null | existing `ConflictKind` members |
| `ComponentHandle<T>.Set<TValue>(Func<T,TValue> selector, NodeHandle target)` — new authoring overload; `NodeHandle.None` — new sentinel | author a handle reference (`c.Set(x => x.target, door)`) and the unambiguous null form (`c.Set(x => x.target, NodeHandle.None)`) | `Set(selector, AssetReference)` |

It **reads** the existing `IdentityMap` LogicalId↔GlobalObjectId mapping (§4) — unchanged — and the
existing `ParseResult.Handles` (LogicalId↔handle-var-name). Two semantic pins:
- A **null** reference is `ValueNode.ObjectRef(TargetLogicalId = null)` ↔ Unity `{fileID: 0}`, authored
  as `NodeHandle.None`.
- Resolution is **two-pass** within a single Materialize (pass 1 creates every object + records its
  identity; pass 2 wires references), so a **mutual** reference (A↔B) and a reference to an object whose
  scene identity is only established this run both resolve. Source-order forward reference of a `var`
  handle is a **C# impossibility** — a local cannot be used before its declaration — so two-pass is a
  materialize-time property, never a source-order one (§Risks).

## Goal
A serialized field on object A can point to another GameObject or Component **B in the same scene**.
The author expresses this with a named handle — the `var door = scene.Add("Door")` value passed into
another call (§6). Sync runs both directions: code sets the in-scene object reference; a reference
rewired in Unity patches the source to the correct handle (or to `NodeHandle.None`).

## In scope
- A component field of type `UnityEngine.Object` (GameObject / Component / MonoBehaviour) referencing
  another node or component **within the same scene**, carried as `ValueNode.ObjectRef(TargetLogicalId)`.
- **Handle authoring:** passing a builder handle (`door`) as the reference target; Core lowers the
  handle identifier to the target's `LogicalId` via `ParseResult.Handles`.
- **Resolution in the adapter:** `LogicalId → GlobalObjectId` (via `IdentityMap`) → live object.
- **Two-pass Materialize:** pass 1 creates/ensures all objects and records their identities; pass 2
  resolves and sets `ObjectRef` fields — so a **mutual reference** (A↔B) or a reference to an object
  whose identity is only established this run resolves. (Source-order forward reference of a `var`
  handle is a C# impossibility; two-pass is a materialize-time property — §Risks.)
- **Materialize (code→scene):** emit `SetReference(path, target)` ops; adapter sets the field's
  `objectReferenceValue` to the resolved in-scene object.
- **Reconcile (scene→code):** detect a rewired reference — the field's `objectReferenceValue` in the
  snapshot now points at a different in-scene object; map new `GlobalObjectId → LogicalId → handle`
  and patch the source argument to the new handle.
- **Null reference:** field set to None (`{fileID: 0}`) ↔ `ObjectRef(TargetLogicalId = null)`, authored
  as `NodeHandle.None`; both directions.
- **Dangling reference:** a reference whose target `LogicalId`/`GlobalObjectId` no longer exists in
  the scene → **`ConflictKind.DanglingReference` / located error** (§7), surfaced, never silently nulled.

## Out of scope
- References to **assets** (materials, meshes, MonoScripts, prefab assets) — that is **M4**
  (`ValueNode.AssetRef`). M5 is scene-object↔scene-object only.
- Cross-**scene** references (multi/additive scenes) — parked (`needs_research`).
- `UnityEvent` / OnClick persistent-listener wiring (target + method + args) — **M8**. M5 handles the
  plain object-reference field only, not the method binding.
- `[SerializeReference]` managed references — **M9**.
- Prefab-instance references and override round-trip — **M6 / M10**.

## Core deliverables
Types **added** (new — mirroring the asset-ref path; grep-verified absent today):
- `ValueNode.ObjectRef(string? TargetLogicalId)` — a new record in the `ValueNode` union, with its
  `[JsonDerivedType(typeof(ValueNode.ObjectRef), "ObjectRef")]` registration and CanonicalJson
  round-trip. `null` TargetLogicalId = None. Default record equality on `TargetLogicalId` is correct
  (both-null counts equal), exactly as `ValueNode.AssetRef` keys on its `Ref`.
- `PlanOp.SetReference(string Path, string? TargetLogicalId)` — a new plan op mirroring `SetAssetRef`
  (`Path` + a nullable target; null/empty = clear the slot). The adapter maps `TargetLogicalId` to the
  live object; Core stays in LogicalIds.
- `ConflictKind.DanglingReference` — a new member of the existing `ConflictKind` enum (today:
  `AmbiguousAnchor, MissingSourceAnchor, ReferencedHandle, DuplicateLogicalId`).

Types **read** (unchanged):
- `IdentityMap.Entries[]` LogicalId↔GlobalObjectId (§4) — the resolution table M5 reads in both
  directions.
- `ParseResult.Handles` (LogicalId↔handle-var-name) — used to lower an authored handle identifier to a
  `LogicalId` at parse, and to emit a `LogicalId` back to its handle name on reconcile.

Functions/behaviors (each a testable contract):
- **Handle lowering:** an authored handle identifier (`door`) passed to `c.Set(x => field, door)` lowers
  to `ValueNode.ObjectRef(<door's LogicalId>)` (resolved via the parser's handle table). `NodeHandle.None`
  lowers to `ObjectRef(null)`.
- **Two-pass resolution:** given a `SceneModel` where A references B — including the **mutual** A↔B case,
  or a B whose scene identity is only established this run — pass 1 creates/records every object's
  identity and pass 2 resolves each `ObjectRef`'s `TargetLogicalId` and emits `SetReference`; no ordering
  error. (Core operates on the already-parsed model, so source declaration order is irrelevant to Core;
  the C# source itself always declares a handle before use.)
- **Materialize → Plan:** a field holding `ObjectRef(id)` produces `SetReference(path, id)`; a field
  holding `ObjectRef(null)` produces `SetReference(path, null)` (clears the slot).
- **Diff:** asset-vs-object aside, two `ObjectRef`s are equal iff their `TargetLogicalId` matches
  (both null counts as equal). A changed `TargetLogicalId` is a change.
- **Reconcile → SourcePatch (rewire):** a snapshot field whose resolved `GlobalObjectId` maps to a
  different `LogicalId` than the parsed source produces a `SourcePatch` swapping the handle argument
  to the new target's handle name.
- **Reconcile → SourcePatch (null):** a snapshot field now `{fileID: 0}` where source had a handle →
  patch the argument to `NodeHandle.None` (None). And the reverse (None → a handle).
- **Cross-ref on/to a newly-created object (§13).** A cross-object ref whose source object OR target was
  editor-created in the same edit resolves against M2b's in-memory `AddedEntry` (§13 rule 1): the ref is
  appended onto the just-created statement, and when the target is ALSO new it resolves two-pass (source
  and target appended in pass 1, the handle wired in pass 2). Where single-pass is infeasible, it is
  reported and converges on a guaranteed second Sync (§13 rule 2) — never a silent null. Cites §13
  (create-with-payload).
- **Dangling → conflict:** a snapshot `GlobalObjectId` that maps to no `IdentityMap` entry (target
  deleted), or a source handle whose `LogicalId` has no live object, produces a
  `ConflictKind.DanglingReference` located conflict naming source object, field, and the missing target —
  never a silent null.

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
- **Component-vs-GameObject target:** a field may be **Component-typed** (`Rigidbody`, `Graphic`, a user
  `MonoBehaviour`) rather than GameObject-typed. When the handle names a GameObject and the field wants a
  component, resolve to the matching component on that object — the `Kind`/`ComponentType` in the
  `IdentityMap` disambiguates (the `LogicalId` must resolve to the component entry, not merely its owner).
- **Two-pass execution:** the adapter executes all create/identity ops before `SetReference` ops so
  every reference has a live target by the time it is assigned.

## Authoring API added
Two additions (mirroring the asset-ref authoring surface):
- **`ComponentHandle<T>.Set<TValue>(Func<T,TValue> selector, NodeHandle target)`** — a new overload
  beside the existing `Set(selector, TValue)` / `Set(selector, AssetReference)`. Passing a handle
  (a `NodeHandle` local) binds here unambiguously (a `NodeHandle` is neither the field's value type nor
  an `AssetReference`).
- **`NodeHandle.None`** — a static, **non-null** sentinel handle meaning "clear this reference to None."
  It exists solely to give an **unambiguous** null form: a bare `c.Set(x => x.target, null)` is
  **CS0121-ambiguous** across the three `Set` overloads, so `null` is never authored directly — the
  author writes `NodeHandle.None`, which binds the `NodeHandle` overload. Core lowers it to
  `ObjectRef(null)`.

A field may be **GameObject-typed** or **Component-typed** (`Rigidbody`, `Graphic`, a user
`MonoBehaviour`, …); the component-target case resolves as in the adapter deliverables above.

```csharp
using UnityEngine;
using SceneBuilder.Authoring;

// A user component carrying a cross-object reference field.
public class DoorOpener : MonoBehaviour { public GameObject target; }

public class FooScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var door = scene.Add("Door");
        scene.Add("Opener")
             .Component<DoorOpener>(c => c.Set(x => x.target, door));            // handle → ObjectRef(door.LogicalId)

        scene.Add("Idle")
             .Component<DoorOpener>(c => c.Set(x => x.target, NodeHandle.None)); // None → ObjectRef(null)
    }
}
```
The handle name is the readable anchor in source; the sidecar carries the GlobalObjectId identity.
Handles are ordinary C# locals — **declared before use**. A **mutual** reference (A↔B) is authored by
declaring both handles first, then setting each reference; two-pass Materialize wires them
(§Core deliverables). Source-order forward reference of a `var` handle does not compile and is not a
supported form.

## IdentityMap / sidecar changes
- No schema change. M5 **reads** `Entries[]` (LogicalId↔GlobalObjectId) for both resolution
  directions and **relies on** GlobalObjectIds being recorded (post-first-save, per §4).
- A reference whose target has `GlobalObjectId == ""` (target never saved yet) resolves within the
  same Materialize via pass-2 LogicalId resolution; the GlobalObjectId is recorded on save as usual.

## Core test plan
IdentityMap and object resolution are exercised as Core data; the live-object lookup is mocked at the
adapter boundary (Core works in LogicalIds/GlobalObjectIds, never touches `UnityEngine`). RED tests:
1. **Handle lowering:** an authored handle identifier → `ObjectRef(handle.LogicalId)`; `NodeHandle.None`
   → `ObjectRef(null)`.
2. **ObjectRef round-trip:** SceneModel → canonical serialize → parse → equal `ObjectRef`
   (`TargetLogicalId` preserved, including null); the new `[JsonDerivedType]` registration round-trips.
3. **Two-pass resolution:** a `SceneModel` with a mutual A↔B reference (or B's identity established this
   run) → Materialize emits `SetReference(pathA, B.LogicalId)` **and** `SetReference(pathB, A.LogicalId)`
   with no ordering failure (Core works on the already-parsed model; source order is irrelevant here).
4. **Materialize → Plan:** `ObjectRef(id)` → `SetReference(path, id)`; `ObjectRef(null)` →
   `SetReference(path, null)`.
5. **Diff:** equal `TargetLogicalId` → no change; changed `TargetLogicalId` → change; null↔handle →
   change.
6. **Reconcile rewire → SourcePatch:** snapshot field's GlobalObjectId maps to a different LogicalId
   than source → patch swaps the handle argument to the new handle name.
7. **Reconcile null → SourcePatch:** snapshot field `{fileID:0}` where source had a handle → patch to
   `NodeHandle.None`; and None→handle reverse.
8. **Dangling ref → conflict:** GlobalObjectId with no IdentityMap entry (deleted target), or handle
   whose LogicalId has no live object → `ConflictKind.DanglingReference` located conflict naming source
   object/field/missing target.
9. **Null round-trip:** `ObjectRef(null)` Materialize→Plan→(mock)→snapshot→Reconcile preserves None.
10. **`Reconcile_CrossRefOnNewObject_Converges`** (§13 create-with-payload). A newly editor-created object
    carrying a cross-object ref in one edit → the handle argument is appended onto that object's
    just-created statement (owner mapped via M2b's `AddedEntry`); when the target is also new it resolves
    two-pass; otherwise reported and converged on a second Sync. Second Sync of the unchanged scene is a
    no-op; never a silent null.

## Unity confirmation checklist
1. Author `FooScene` where `Opener` (a `DoorOpener`) references the `Door` handle **and** `Door` in turn
   references `Opener` — a **mutual** A↔B reference (both handles declared, then each wired); run
   Materialize. **Expected:** in the Inspector, Opener's `target` points at the Door GameObject **and**
   Door's `target` points at Opener — two-pass resolved both, no ordering error.
2. In Unity, drag a **different** GameObject into `Opener`'s `target` field; trigger Reconcile.
   **Expected:** source updates the handle argument to the new target's handle (e.g.
   `.Set(x => x.target, otherHandle)`).
3. In Unity, set `Opener`'s `target` field to **None**; trigger Reconcile. **Expected:** source
   reflects None (`.Set(x => x.target, NodeHandle.None)`).
4. Delete the `Door` GameObject while `Opener` still references it; run sync. **Expected:** a loud,
   located `ConflictKind.DanglingReference` naming `Opener > DoorOpener.target` and the missing target —
   the field is not silently cleared.
5. Re-run Materialize after step 2/3. **Expected:** scene matches source, no spurious diff (idempotent
   round-trip).
6. **Component-typed target:** author a field that references a specific Component (e.g. a
   `HingeJoint.connectedBody` pointing at another object's `Rigidbody`); Materialize. **Expected:** the
   slot resolves to the **component** on the target object, not merely its GameObject.

## Dependencies
- **M2** — Snapshot reader, Reconcile, and Roslyn `SourcePatch` (argument-swap mechanism reused).
- **M3** — components + serialized fields (the field a reference lives on) and generic `.Set`.
- **M1** — Materialize → Plan and the IdentityMap with GlobalObjectIds (the resolution table M5
  reads).
- **§20 (unqualified type names, shipped)** — user-script component types (`DoorOpener`) resolve by
  short name; M5's examples rely on that already being in place.

## Risks/notes
- **Source-order forward reference of a `var` handle does not compile — do not design for it.** C#
  forbids using a local before its declaration, so `c.Set(x => x.target, door)` must appear after
  `var door = ...`. The two-pass Materialize exists for the cases source order cannot express by itself
  — a **mutual** A↔B reference (both handles declared, both references then wired) and a reference whose
  target's scene identity is only minted this run. Core sees an already-parsed model, so source order is
  irrelevant to it; the compile constraint lives entirely in the authored `.cs`.
- **GlobalObjectId lifecycle:** GlobalObjectIds exist only after first save (§4). A reference authored
  before the target is ever saved must resolve via pass-2 LogicalId resolution, not via a (still
  empty) GlobalObjectId — do not assume the id is populated at resolution time.
- **Rewire vs. delete ambiguity:** distinguish "target changed to another live object" (rewire →
  patch handle) from "target removed / points at nothing valid" (null vs. dangling). A ref to a
  deleted object is a **`ConflictKind.DanglingReference`**, not an implicit null; a ref explicitly set
  to None is a legitimate null.
- **Prefab-instance targets** keyed on `(targetPrefabId, targetObjectId)` (§4) are deferred to M6;
  M5 covers plain in-scene objects.
- **Component vs. GameObject target:** a field may reference a specific Component (e.g. a `Rigidbody`)
  rather than its GameObject; the `LogicalId` must resolve to the component entry, not just its owner,
  so `Kind`/`ComponentType` from the `IdentityMap` disambiguates.
- **Unambiguous null is load-bearing.** `c.Set(x => x.target, null)` is CS0121-ambiguous across the
  `Set(selector, TValue)` / `Set(selector, AssetReference)` / `Set(selector, NodeHandle)` overloads;
  `NodeHandle.None` (a non-null sentinel) is the only null form, and both the parser (recognize
  `NodeHandle.None` → `ObjectRef(null)`) and the emitter (emit `NodeHandle.None` for a null ref) must
  use it. Generated C# must compile (foundation), so a null ref emitted as bare `null` would be a bug.
