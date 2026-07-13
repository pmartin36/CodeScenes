# M6 — Prefab instances (whole instance; no override round-trip)

### Additions to the contract

This milestone introduces types not present in foundation §3. Each is flagged here per §1.

- **`PrefabInstanceNode`** — a `GameObjectNode` variant representing the root of a prefab instance in
  a `SceneModel`/`SceneSnapshot`. It carries everything a `GameObjectNode` carries (`LogicalId`,
  `Name`, `Tag`, `Layer`, `Active`, `IsStatic`, `Transform`, `Children`) plus:
  ```
  PrefabInstanceNode : GameObjectNode
    SourcePrefab      : AssetRef          // the prefab asset, keyed by GUID (FileId = 0 = main asset)
    OpaqueOverrides   : Unsupported?      // m_Modification.m_Modifications, verbatim, read-only (M10)
  ```
  `Components` on a `PrefabInstanceNode` is empty in v1 (component-level content lives inside the
  prefab and is not modelled until M10). The node is the whole instance as one unit.
- **`PrefabInstanceKey`** — the identity pair from foundation §4, made explicit as a POCO so the
  IdentityMap can persist both halves:
  ```
  PrefabInstanceKey
    TargetPrefabId  : ulong    // GlobalObjectId.targetPrefabId  (the source prefab asset)
    TargetObjectId  : ulong    // GlobalObjectId.targetObjectId  (the object within the instance)
  ```
  For a plain (non-prefab) object `TargetPrefabId == 0`; a nonzero value is what marks a snapshot
  object as belonging to a prefab instance.

`AssetRef`, `ValueNode.Unsupported`, `GameObjectNode`, and `GlobalObjectId` are used verbatim per §3.

---

## Goal

Author a prefab **instance** into a scene from code — `scene.Instance("Assets/Prefabs/Enemy.prefab")`
— and keep its presence, root transform, and hierarchy placement in bidirectional sync. The instance
is handled as one whole unit; per-property overrides are preserved but not yet reflected in code.

## In scope

- Modelling a prefab instance root as a `PrefabInstanceNode` referencing its source prefab by GUID.
- **Materialize (code→scene):** instantiate the prefab into the scene at the node's position in the
  hierarchy with the node's root transform.
- **Reconcile (scene→code):** detect a prefab instance added, removed, or moved/reparented/reordered
  in the live scene and emit the corresponding `SourcePatch`.
- Identity keyed on the `(TargetPrefabId, TargetObjectId)` **pair** from `GlobalObjectId`, persisted
  in the IdentityMap (§4).
- Per-property overrides (`m_Modification.m_Modifications`) read as **opaque** into
  `PrefabInstanceNode.OpaqueOverrides` and **preserved** across round-trips, flagged, never dropped.
- Root-transform round-trip for the instance (position/rotation/scale of the instance root), reusing
  the M1/M2 transform path.

## Out of scope

- **Per-property override round-trip** (`m_Modifications` authored/reflected in code) — deferred to
  **M10**. v1 treats overrides as opaque preserved bytes only.
- Added/removed components and added/removed child GameObjects *within* an instance (also M10).
- Nested prefabs / prefab variants as first-class authored concepts (the source asset may itself be a
  variant; v1 only instantiates by GUID and does not model the variant chain).
- Editing the prefab *asset* itself (SceneBuilder authors scenes, not prefab assets).
- Unpacking an instance, or converting a plain object into an instance in place.

## Core deliverables

**Types added/changed (referencing §3):**
- Add `PrefabInstanceNode` (GameObjectNode variant) and `PrefabInstanceKey` (see additions above).
- `SceneModel.Roots` and `GameObjectNode.Children` may contain `PrefabInstanceNode`s; the canonical
  serializer emits them with a distinct, deterministic form (source-prefab GUID + root transform +
  opaque-override token).

**Functions/behaviors (each a testable contract):**
- **Model→Plan (instantiate):** `Materialize` of a `SceneModel` containing a new `PrefabInstanceNode`
  (no matching IdentityMap entry) produces a Plan with an `InstantiatePrefab(guid, parentLogicalId,
  siblingIndex)` op followed by the root-transform `SetField` ops — never `CreateObject` +
  `AddComponent`.
- **Model→Plan (remove):** a `PrefabInstanceNode` present in the map/snapshot but absent from desired
  source lowers to a single `DestroyObject` op targeting the instance root.
- **Model→Plan (move):** an instance whose parent/sibling-index differs between desired and actual
  lowers to `SetParent`/`ReorderChild` on the instance root only — no destroy/re-instantiate (identity
  preserved).
- **Pair-key identity:** the differ matches a desired `PrefabInstanceNode` to a snapshot instance via
  the IdentityMap `LogicalId ↔ (TargetPrefabId, TargetObjectId)` pair. Two instances of the *same*
  prefab (same `TargetPrefabId`, different `TargetObjectId`) are distinct entries and never collide.
- **Overrides opaque + preserved:** parsing a snapshot whose instance has `m_Modifications` yields a
  `PrefabInstanceNode.OpaqueOverrides = Unsupported(raw)`; a Materialize/Reconcile no-op cycle leaves
  the token byte-identical; the token is surfaced as a flag (per §7), never silently consumed.
- **Reconcile (added):** a snapshot instance with no IdentityMap entry produces a `SourcePatch` that
  appends a `scene.Instance("<re-derived path>")…` statement.
- **Reconcile (removed):** an instance in source but absent from snapshot produces a delete-statement
  patch.
- **Canonical stability:** serialize→parse→serialize of a `PrefabInstanceNode` is byte-identical,
  including the opaque override token and the source-prefab GUID.

## Editor adapter deliverables

- **Execute `InstantiatePrefab`:** load the prefab asset by GUID via `AssetDatabase.GUIDToAssetPath`
  + `AssetDatabase.LoadAssetAtPath`, instantiate with `PrefabUtility.InstantiatePrefab` into the
  target scene, parent/order it per the op, then apply root-transform ops via `SerializedObject`.
- **Read instances into the snapshot:** for each scene root/child, detect a prefab instance
  (`PrefabUtility.IsPartOfPrefabInstance` / `GetPrefabInstanceStatus`), read the source asset GUID
  (`PrefabUtility.GetCorrespondingObjectFromSource` → `AssetDatabase.GUIDFromAssetPath`), stamp the
  node's `GlobalObjectId`, and split it into `PrefabInstanceKey { TargetPrefabId, TargetObjectId }`
  via `GlobalObjectId.targetPrefabId` / `.targetObjectId`.
- **Read overrides opaquely:** capture the instance's `m_Modification.m_Modifications` block as a raw
  token into `OpaqueOverrides` (no interpretation), so it survives write-back.
- **Resolve** the source-prefab GUID↔path via `AssetDatabase` (reuse the M4 asset resolver).

## Authoring API added

Introduces `scene.Instance(assetPath)` returning a handle that supports `.Transform(...)`, `.Id(...)`,
and hierarchy nesting like `.Add`, but **not** `.Component<T>()` or field setters (whole-instance only
in v1).

```csharp
public class ArenaScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var enemy = scene.Instance("Assets/Prefabs/Enemy.prefab")
                         .Transform(pos: (3, 0, 5), euler: (0, 90, 0));
        scene.Instance("Assets/Prefabs/Enemy.prefab").Transform(pos: (-3, 0, 5)); // 2nd, distinct instance
        var pack = scene.Add("Pickups");
        pack.Instance("Assets/Prefabs/Coin.prefab").Transform(pos: (0, 1, 0));    // nested under a plain node
    }
}
```

The `assetPath` is authoring-time convenience only; the **GUID** is authoritative and stored in the
sidecar (§4). Display path is re-derived from the GUID on read.

## IdentityMap / sidecar changes

- Extend each `Entries[]` record with an optional prefab-instance identity:
  ```
  Entries[]:
    LogicalId
    GlobalObjectId
    Kind                 // GameObject | Component | PrefabInstance   (new value)
    ComponentType?
    ParentLogicalId?
    PrefabKey?           // { TargetPrefabId, TargetObjectId }   (new; present when Kind = PrefabInstance)
    SourcePrefabGuid?    // new; the instantiated prefab asset's GUID
  ```
- The instance's source prefab asset is recorded in the existing `Assets[]` cache
  (`Guid, LastKnownPath, TypeHint = "Prefab"`) so its display path re-derives on rename/move.
- `PrefabKey` persistence is what lets two instances of the same prefab stay distinct across sessions;
  identity matching uses `(TargetPrefabId, TargetObjectId)`, never `SourcePrefabGuid` alone.

## Core test plan

RED tests `tdd-pipeline` will write (behaviors, headless, no Unity):

1. **Instantiate → Plan:** desired model with one new `PrefabInstanceNode` ⇒ Plan is
   `[InstantiatePrefab(guid,parent,idx), SetField(position…), SetField(rotation…), SetField(scale…)]`
   in that order; no `CreateObject`/`AddComponent`.
2. **Remove → Plan:** instance in map+snapshot, absent from desired ⇒ Plan is `[DestroyObject(root)]`.
3. **Move → Plan:** same instance, changed parent/sibling-index ⇒ Plan is `[SetParent]`/`[ReorderChild]`
   only; no destroy/instantiate; LogicalId and pair-key unchanged.
4. **Pair-key identity:** two `PrefabInstanceNode`s of the same prefab GUID map to two IdentityMap
   entries differing only in `TargetObjectId`; differ matches each to the correct snapshot instance;
   swapping their transforms does not cross-assign.
5. **Overrides opaque + preserved:** snapshot instance with a nonempty `m_Modifications` ⇒
   `OpaqueOverrides` populated; serialize→parse→serialize byte-identical; a desired==actual cycle
   yields an empty Plan (no spurious ops) and emits the "overrides preserved, not modelled" flag.
6. **Move round-trip:** apply a snapshot move to source via Reconcile→SourcePatch, re-parse, re-materialize
   ⇒ no-op Plan (idempotent).
7. **Reconcile added/removed:** snapshot-only instance ⇒ append `scene.Instance(path)` statement;
   source-only instance ⇒ delete-statement patch; both located per §7.
8. **Canonical determinism:** two independent serializations of the same `PrefabInstanceNode` are
   byte-identical.

## Unity confirmation checklist

Steps the user performs on a real Unity 6 project; expected result each step:

1. Author `ArenaScene` with one `scene.Instance("Assets/Prefabs/Enemy.prefab")`, build →
   **an Enemy prefab instance appears in the scene** (blue prefab icon), at the authored transform,
   with its GlobalObjectId recorded to the sidecar on save.
2. Add a second `scene.Instance(...)` of the same prefab, build → **two distinct instances**; sidecar
   has two entries with the same `SourcePrefabGuid` and different `TargetObjectId`.
3. In Unity, **move** the instance (translate + reparent under "Pickups"), save, refocus →
   Reconcile updates the source `.Transform(...)`/nesting; the instance is **not** re-created
   (GlobalObjectId unchanged).
4. In Unity, **override a property** on the instance (e.g. change a child's material) → the instance
   shows the override (bold/blue), save, refocus → **no corruption**: source is unchanged for that
   property, the sidecar/opaque override is preserved, and the tool **flags** "override present, not
   yet reflected in code (M10)". Re-building does not revert the override.
5. **Delete** the instance in Unity, refocus → Reconcile removes the `scene.Instance(...)` statement.

## Dependencies

- **M1/M2** — hierarchy + transform Materialize/Reconcile and SourcePatch (root transform reuse).
- **M4** — asset path↔GUID resolve/persist and `Assets[]` cache (source-prefab GUID).
- IdentityMap read/write (§4) and the Plan/Snapshot seam (M0).

## Risks/notes

- `GlobalObjectId` for instance objects is a pair (`targetPrefabId`,`targetObjectId`); storing only a
  single fileID would collide sibling instances — the sidecar **must** persist `PrefabKey` (see §4).
- Opaque overrides are a large, structured YAML block; treat strictly as `Unsupported(rawToken)` —
  do not attempt partial parsing in v1 or M10's contract becomes ambiguous.
- Re-instantiation on move is the main hazard: the executor MUST reconcile-into-existing (§5, never
  wipe-and-recreate) to preserve the instance's GlobalObjectId and its user overrides.
- Prefab asset deleted/moved on disk ⇒ GUID resolves to missing; surface a located missing-asset
  error (§7), do not silently drop the instance.
