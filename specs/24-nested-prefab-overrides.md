# M-nested-props — Nested-target prefab overrides + added/removed child GameObjects

> **Addressing shape: nested targets are addressed by the SHIPPED compiler-checked TYPED selectors, not string paths.**
> Nested targets are addressed with the typed member-chain selector
> `scene.Instance(Prefabs.Tank).On(sel => sel.Turret.Barrel, …)` delivered by the **typed-prefab-façades**
> milestone (`specs/25-typed-prefab-facades.md`, **SHIPPED** — a **HARD dependency** this milestone consumes).
> Rationale (the project's core principle): a string path is not compiler-checked — a typo or a stale path
> after a Unity rename slips past the compiler, which is unacceptable. The typed façade makes the address
> compiler-verified and makes a rename **auto-sync or fail to compile**. Only the nested ADDRESSING form is
> typed; the identity model (durable pair-key in the sidecar, `ChildPath` re-derived) is unchanged — the
> typed selector resolves (through the façade manifest, spec 25) to the SAME durable target the string form
> did, and spec 25 lowers the typed and string forms identically. The string `.On("Turret/Barrel", …)` form
> is retained as a supported fallback (spec 25 behavior 10); the typed selector is the preferred form and is
> what codegen/Reconcile emit.

> This milestone finishes the two things **M10** (`specs/11-m10-prefab-overrides.md`) explicitly
> deferred in its v0 banner:
> - **Banner #1 — nested-target addressing.** M10 round-trips overrides / added / removed **components**
>   that target the instance **root's own** components only. Any override on an object **below** the
>   root — a nested child GameObject, a component below the root, including one inside a **nested
>   prefab** — is currently classified `Nested` and dumped into `PrefabInstanceNode.OpaqueOverrides`
>   (the M6 read-as-opaque-and-preserved residue, `PrefabInstanceProbe.ClassifyTarget` →
>   `ModTargetKind.Nested`). This milestone makes those first-class structured overrides.
> - **Banner #5 — added / removed child GameObjects.** M10 models `AddedGameObjects` /
>   `RemovedGameObjects` on `PrefabInstanceNode` for forward-compat but neither authors nor round-trips
>   them, because they had no authoring surface and collide with M6's `instance.Add`→`Children`
>   semantics. This milestone gives them an authoring surface and resolves that collision.
>
> Everything M10 shipped (root-target property overrides, added/removed **root** components, materialize
> revert, stale-override conflict, deterministic serialization) is a hard dependency and is not
> re-specified here.

### Additions to the contract

M-nested-props adds no new top-level POCO, but **extends two existing M10 additions** and one sidecar
record. All value payloads continue to reuse §3 `ValueNode`, `AssetRef`, `ObjectRef`, `ComponentData`,
`GameObjectNode` verbatim — no parallel value model is invented.

```
OverrideTarget                                  // M10 type; the pair-key is REDEFINED to address a
                                                //   sub-object INSIDE the instance, not just the root.
  SubKey        : PrefabInstanceKey             // (TargetPrefabId, TargetObjectId) of the target
                                                //   GameObject INSIDE the instance — the DURABLE,
                                                //   rename-stable identity (§4 pair-key, reused from M6).
                                                //   Root target: the instance root's own key.
                                                //   Sidecar-persisted. NEVER a name path.
  ComponentType : string                        // FullName of the target component ("" when the mod
                                                //   targets the GameObject itself, e.g. m_IsActive).
                                                //   Distinguishes which component on SubKey's object.
  ChildPath     : string                        // "/"-joined NAME path, instance root → target object,
                                                //   RE-DERIVED from SubKey at read time, NON-authoritative
                                                //   (display + authoring only). "" == instance root.
// Identity/equality is (SubKey, ComponentType) ONLY. ChildPath is display; a rename re-derives it while
// (SubKey, ComponentType) stays stable. This RETIRES M10's root-only "type:"+FullName sigil in PrefabId
// (PrefabInstanceProbe.Overrides.cs / BuilderParser.Instance.cs:112,243 / ReconcilerInstances.cs:359):
// root targets become the ChildPath == "" case with SubKey = the root's own pair-key.

PrefabInstanceNode.AddedGameObjects  : GameObjectNode[]    // M10 field; NOW authored + round-tripped.
                                                           //   Each carries a Parent: OverrideTarget
                                                           //   (SubKey of the in-instance parent; "" == root)
                                                           //   naming where under the instance it hangs.
PrefabInstanceNode.RemovedGameObjects: OverrideTarget[]    // M10 field; NOW authored + round-tripped.
```

`AddedComponent.Target` (M10) already an `OverrideTarget`; under this milestone its `SubKey` may name a
**nested** object, not just the root. `PropertyOverride.Target` likewise. No shape change to
`AddedComponent` / `PropertyOverride` beyond the redefined `OverrideTarget` they already hold.

**Sidecar (`IdentityMapEntry.Overrides : InstanceOverrideRecord[]`) extension** — `InstanceOverrideRecord`
persists the durable sub-object key so a nested target still resolves across reload with no scene-local
fileID and no GUID in source:
```
InstanceOverrideRecord            // M10 record; PrefabId(string)/ObjectId(long) REPLACED by the pair-key
  SubKey        : PrefabInstanceKey   // { TargetPrefabId, TargetObjectId } — the durable sub-object id
  ComponentType : string
  PropertyPath  : string              // "" for added/removed-component and child-GO records
  Kind          : "Property" | "AddedComponent" | "RemovedComponent" | "AddedGameObject" | "RemovedGameObject"
  BaseValue     : string?             // as M10 (stale-override detection); property records only
```

---

## Goal

Author and round-trip prefab-instance overrides that target an object **below** the instance root — a
nested child, a component below the root, a target inside a **nested** prefab — plus **added and
removed child GameObjects** on an instance. A user can tweak a deep sub-object of a prefab in code, or
add/remove/edit a child of an instance in Unity, and it round-trips without ever writing a
GlobalObjectId or GUID into the clean C# source.

## In scope

- **Nested-target property overrides.** A `PropertyModification` whose target is any object below the
  instance root lowers to a structured `PropertyOverride` keyed on the target sub-object's durable
  `(TargetPrefabId, TargetObjectId)` pair (`OverrideTarget.SubKey`), including targets that live inside
  a **nested prefab** within the instance.
- **Nested added / removed components.** `AddComponent` / `RemoveComponent` on a sub-object (not only
  the root) — `AddedComponent`/`RemovedComponents` whose `Target.SubKey` names a nested object.
- **Added / removed child GameObjects** (banner #5): author, materialize, and reconcile
  `AddedGameObjects` (a scene child not present in the source prefab) and `RemovedGameObjects` (a source
  child stripped on the instance), via `PrefabUtility.GetAddedGameObjects` / `GetRemovedGameObjects`.
- **The `instance.Add(...)` → `AddedGameObjects` reconciliation** (design decision, below): a non-prefab
  child added under a prefab instance in the scene is, by Unity's data model, an `AddedGameObjects`
  override — so it is reconciled as one, not as an M6 plain `Children` node.
- **Durable, rename-safe sub-object addressing** via `OverrideTarget.SubKey` (the pair-key), persisted
  in the sidecar; a human-readable `ChildPath` re-derived for authoring/display.
- Read side **undoes the M10 opaque-collapse** for below-root targets: the snapshot reader enumerates
  inside the instance far enough to resolve each override target to its durable pair-key.

## Out of scope

- **Authoring nested-prefab STRUCTURE** (creating prefab-in-prefab assets, prefab variants as an
  authored concept). This milestone addresses objects inside an already-nested prefab instance; it does
  not build the nesting. (Same boundary M6/M10 drew.)
- **Re-exposing instance internals as ordinary `SnapshotNode` children.** The instance stays ONE node
  (M6). Enumeration inside it is override-capture only; sub-objects are NEVER emitted as regular
  `GameObjectNode`s (that would fight the whole-unit model and re-introduce per-keystroke full-descent
  cost). Only the override collections gain nested entries.
- Reordering children *within* an instance as an authored concept (a reorder of instance-internal
  siblings that Unity records as a modification stays opaque/preserved, not authored, this pass).
- UnityEvent / `[SerializeReference]` values inside a nested override beyond what M8/M9 already model; a
  nested override whose value is such a field reuses those models unchanged.
- The M10-owned pieces (root-target overrides, materialize revert, stale-override conflict) — depended
  on, not re-specified.

## Core deliverables

### Types added/changed (referencing §3)

- **`OverrideTarget` redefined** (above): `SubKey : PrefabInstanceKey`, `ComponentType : string`,
  `ChildPath : string`; equality on `(SubKey, ComponentType)`. Retires the root-only `"type:"+FullName`
  sigil. `PrefabInstanceKey` (M6) is reused verbatim as the durable sub-object identity.
- **`PrefabInstanceNode.AddedGameObjects` / `RemovedGameObjects`** promoted from modelled-only to
  authored + round-tripped. `AddedGameObjects` entries gain a `Parent : OverrideTarget` naming the
  in-instance parent object.
- **`InstanceOverrideRecord`** (sidecar) carries `SubKey` + `ComponentType` + `Kind` (above).
- No change to `SceneModel`, `GameObjectNode`, `ComponentData`, `ValueNode`, `AssetRef`, `ObjectRef`,
  `PropertyOverride`'s own fields, `AddedComponent`'s own fields.

### Functions / behaviors (testable contracts)

1. **Nested target lowers to a real pair-key, not a name path, not a type sigil.** Given a snapshot
   `PropertyModification` whose target is a component on a nested child, the reader produces a
   `PropertyOverride` whose `Target.SubKey == (TargetPrefabId, TargetObjectId)` of that child's live
   object and whose `Target.ComponentType` is the component's FullName. `ChildPath` is re-derived and
   carries the name path, but two overrides that differ **only** in `ChildPath` (same `SubKey`,
   `ComponentType`) are the SAME target — a name-path change alone never re-keys an override. *(This is
   the spec-16 hazard the addressing scheme must avoid: name-path-as-identity is rejected.)*
2. **Rename re-derives the path, keeps the key.** Renaming a nested child (an override on it) yields a
   snapshot whose override has an unchanged `SubKey`/`ComponentType` and a new `ChildPath`; Reconcile
   emits at most a display-path patch, NEVER a drop-and-re-add of the override. Round-trip is
   identity-stable across the rename.
3. **Target inside a nested prefab is addressable.** An override on a component that lives inside a
   nested prefab (a prefab instantiated within the outer prefab) lowers to a `PropertyOverride` with the
   nested object's own pair-key; write-back reconstructs the correct source object *inside that nested
   prefab* (adapter uses `GetCorrespondingObjectFromSourceAtPath`, below). Round-trip is byte-stable.
4. **Nested added component.** A component present on a nested instance child but absent from the source
   prefab lowers to `AddedComponent{Target.SubKey = <child key>, Component}`; materialize adds it on that
   child; reconcile appends `.On(sel => sel.<Child>, x => x.AddComponent<T>())` (find-or-create the
   sub-object's `.On` block, see behavior #10).
5. **Nested removed component.** A source component stripped on a nested child lowers to a
   `RemovedComponents` entry whose `SubKey` names the child; round-trips as
   `.On(sel => sel.<Child>, x => x.RemoveComponent<T>())`.
6. **Added child GameObject ↔ `AddedGameObjects`.** A scene child under an instance that is not part of
   the source prefab lowers to an `AddedGameObjects` `GameObjectNode` with `Parent.SubKey` = the
   in-instance parent's key; materialize creates it under that parent and reconciles/preserves it in
   place (never re-instantiates the whole prefab). Reconcile emits an `.AddChild(...)` authoring call.
7. **Removed child GameObject ↔ `RemovedGameObjects`.** A source child stripped on the instance lowers
   to a `RemovedGameObjects` `OverrideTarget`; round-trips as `.RemoveChild("<childPath>")`.
8. **`instance.Add(...)` reconciles to `AddedGameObjects`, not `Children`** *(design decision touching
   shipped M6)*. In `SceneModel`, a child authored under an `InstanceHandle` (M6's `instance.Add`) whose
   node is NOT itself a prefab instance is lowered as an `AddedGameObjects` entry of that instance, not
   as a plain `GameObjectNode` in `Children`. Reconcile of a hand-added scene child under an instance
   likewise produces an `AddedGameObjects`/`.AddChild(...)` result, never a top-level append. The Core
   materializer routes such a node through the added-GameObject-override path. *(A child that is itself
   `scene.Instance(...)` under an instance remains a nested instance node, unchanged.)*
9. **Materialize applies AND reverts, at nesting depth.** Materialize of a `PrefabInstanceNode` produces
   `Plan` ops that set each nested property override, add/remove the listed nested components, and
   add/remove the listed child GameObjects **in place** on the existing instance (preserving its and
   every sub-object's `GlobalObjectId`; never re-instantiate). A nested override / added component /
   added child present on the LIVE instance but ABSENT from the desired model is **reverted** via the
   appropriate `PrefabUtility` revert op (M10 banner #6), never left as a stale bold override.
10. **Reconcile detects nested instance edits, grouping per sub-object under one `.On`.** A snapshot whose
    instance carries nested mods / adds / removes not present in the parsed `SceneModel` emits a
    `SourcePatch` scoping each below-root edit under a typed `.On(sel => sel.<Child>, x => x.<ops>)` on the
    instance's source statement, span-local; child-adds / child-removes stay top-level as `.AddChild(...)` /
    `.RemoveChild("<path>")`. **Multiple nested edits on the SAME sub-object MUST be emitted as ONE
    `.On(sel => sel.<Child>, x => x.op1().op2())` block, never as repeated `.On(sel => sel.<Child>, …)`
    calls for the same target.** The SourceEdit machinery FINDS-OR-CREATES the `.On(sel => …, …)` closure
    for a sub-object — matching on the selector's RESOLVED target (the durable child, via the façade
    manifest, spec 25), not on the literal selector text — and appends each op INSIDE that closure (more
    Roslyn work than M10's flat appends: locate an existing `.On` invocation whose selector resolves to the
    same sub-object in the span, descend into its closure lambda body, and append the op there; only when no
    such `.On` exists is a new one created). Revert (snapshot no longer carries an authored nested override)
    drops the corresponding op from inside its `.On` block, and drops the now-empty `.On` call entirely when
    its last op is removed (M10 behavior #8, extended to nested + child verbs). The emitted selector is the
    typed form; a string `.On("<path>", …)` already present in source is matched as a fallback (spec 25).
11. **Depth-safe determinism.** Canonical serialization orders nested `Overrides` by
    `(SubKey.TargetPrefabId, SubKey.TargetObjectId, ComponentType, PropertyPath)`, `AddedGameObjects` by
    `(Parent.SubKey, Name)`, `RemovedGameObjects` by `SubKey`, so repeated round-trips at any depth are
    stable (§8). `ChildPath` never participates in ordering (it is non-authoritative).
12. **Pair-uniqueness is asserted, not assumed** *(open-risk gate; see Risks/notes)*. Core exposes the
    match as `(SubKey, ComponentType)` lookup; the adapter is REQUIRED (Unity confirmation #7) to prove
    the bare pair is unique at nesting depth ≥ 2 before Core relies on it, with the correspondence chain
    (`GetCorrespondingObjectFromSourceAtPath` + instance-scoped queries) as the authoritative fallback
    if it is not.

## Editor adapter deliverables

- **Undo the opaque-collapse for below-root targets** (`SceneSnapshotReader` / `PrefabInstanceProbe`).
  `SceneSnapshotReader.ReadNode` still returns the instance as ONE node with empty `Children`
  (`SceneSnapshotReader.cs:53-65`, unchanged — no sub-objects surface as regular nodes), but
  `PrefabInstanceProbe.ReadInstanceRoot` now enumerates the transforms INSIDE the instance to build a
  **source-object → live-instance-object map** (`PrefabUtility.GetCorrespondingObjectFromSource` per
  descendant), so each `PropertyModification.target` (a source-asset object) resolves to the live
  sub-object and thence to its `GlobalObjectId` pair-key. `ClassifyTarget` no longer routes below-root
  targets to `ModTargetKind.Nested`/opaque; it lowers them to structured `PropertyOverride`s with the
  resolved `SubKey`.
- **Read added/removed child GameObjects.** Use `PrefabUtility.GetAddedGameObjects(root)` →
  `AddedGameObjects` (each `AddedGameObjectOverride.instanceGameObject` read via the existing
  `SerializedFieldBridge`/node reader, its `Parent.SubKey` = the parent transform's pair-key) and
  `PrefabUtility.GetRemovedGameObjects(root)` → `RemovedGameObjects` (present in the project's
  6000.5; do NOT gate it behind a version check — it exists). If ever absent, flag unsupported, never
  drop (§7).
- **Write mechanics — nested property overrides:**
  - Get/Set the modification list on the **outermost** instance root
    (`PrefabUtility.GetOutermostPrefabInstanceRoot`) via `GetPropertyModifications` /
    `SetPropertyModifications`, not on the sub-object.
  - Each `PropertyModification.target` MUST be the **SOURCE** object, obtained with
    `PrefabUtility.GetCorrespondingObjectFromSource` for a directly-owned target, or
    `PrefabUtility.GetCorrespondingObjectFromSourceAtPath` to walk to a source object **inside a specific
    nested prefab** (the nested-prefab case in behavior #3).
  - **Filter `GetPropertyModifications` noise** — Unity's bookkeeping mods (`m_RootOrder`, transform
    sibling index, `m_Name` on the root already modelled) are excluded exactly as M10 excludes the root
    equivalents; the filter now runs per sub-object.
- **Write mechanics — nested add/remove component & child GO:** add a component to a nested sub-object
  via the `PrefabUtility` add-component flow on the resolved live sub-object; remove via the remove flow;
  create an added child under the resolved in-instance parent and reconcile-into-existing (never
  re-instantiate the instance); revert an added child / added component / override via the matching
  `PrefabUtility` revert call.
- **Resolve `OverrideTarget.SubKey` ↔ live sub-object** in both directions: read = live descendant →
  `GlobalObjectId` pair; write = pair (or re-derived `ChildPath` fallback) → live descendant →
  `GetCorrespondingObjectFromSource(AtPath)` → source object. The `ChildPath` walk from the outermost
  root is the authoritative fallback when a bare pair fails to disambiguate (open-risk mitigation).
- No override *logic* in the adapter — it reads the lists, resolves pairs, and executes the Plan.

## Authoring API added

Extends M6's `scene.Instance(...)` handle and M10's root `.Override(...)`, and consumes spec 25's typed
`Instance(Prefabs.X)` overload + typed `.On` selector overload (`InstanceHandle<TRef>.On<TNode>`). **Root-target
ops stay the direct M10 verbs on the instance handle, unchanged** (`.Override(cfg)`, `.AddComponent<T>([cfg])`,
`.RemoveComponent<T>()`). **Below-root (nested) ops are authored by SCOPING to the sub-object** with the
SHIPPED typed selector `.On(selector, closure)` — `selector` is a compiler-checked member chain
`sel => sel.Turret.Barrel` (spec 25) from the instance's typed root ref to the sub-object, and the closure
receives a scoped handle on which nested ops are stacked. **The selector is authoring/display convenience
only; identity is the sidecar pair-key** (§4, spec-16 principle: no GlobalObjectId/GUID in clean source).
The selector resolves through the façade manifest (spec 25) to the same durable child the string path did;
a rename auto-rewrites the accessor (spec 25 reference-patch) or fails to compile, and the sidecar key is
unchanged. The string `.On("childPath", closure)` form is retained as a supported fallback (spec 25
behavior 10); typed is preferred.

```csharp
public class ArenaScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        scene.Instance(Prefabs.Tank)                       // typed prefab ref (spec 25); "path" still works
             .Transform(pos: (3, 0, 5))

             // root (M10) — unchanged
             .Override(t => t.Set((TankController c) => c.health, 200))
             .AddComponent<AudioSource>()
             .RemoveComponent<Rigidbody>()

             // nested — scope once (typed selector, spec 25), stack ops
             .On(sel => sel.Turret.Barrel, barrel => barrel
                 .Override(b => b.Set((Light l) => l.intensity, 4f))
                 .AddComponent<AudioSource>())
             .On(sel => sel.Turret.Antenna, a => a.RemoveComponent<MeshRenderer>())

             // structural children — top-level
             .AddChild("Turret", "MuzzleFlash", mf => mf.Component<Light>(l => l.Set(x => x.range, 5f)))
             .RemoveChild("Turret/Antenna");
    }
}
```

- `.On(sel => sel.A.B, x => …)` scopes to the sub-object named by the typed selector and runs the closure
  against a scoped handle `x`. `x` exposes the same override verbs as the root handle — `.Override(cfg)`,
  `.AddComponent<T>([cfg])`, `.RemoveComponent<T>()` — each returning `x` for chaining, so multiple ops on
  the SAME sub-object stack inside ONE `.On(...)` block. Every op lowered under `.On(sel => …, …)` gets
  `Target.SubKey` = the child's pair-key (resolved by the adapter on build/save, from the selector's
  manifest-resolved durable child, spec 25) and `Target.ChildPath` = the re-derived name path. The string
  `.On("<path>", …)` fallback lowers identically (spec 25 behavior 10).
- **Root ops stay direct, not scoped.** `.Override(cfg)` / `.AddComponent<T>([cfg])` /
  `.RemoveComponent<T>()` called straight on the instance handle (no `.On`) are the root case
  (`ChildPath == ""`), exactly the M10 verbs.
- `.AddChild(parentPath, name[, cfg])` lowers to an `AddedGameObjects` node (`parentPath` "" == root);
  the closure reuses the full `NodeHandle` surface (components/fields/refs). Structural child ops stay
  **top-level on the instance handle** — NOT inside `.On`. **This supersedes using `instance.Add(...)`
  for a non-prefab child under an instance** — see behavior #8; `instance.Add` authored under an instance
  now lowers to an added-GameObject override, and `.AddChild` is its explicit, parent-addressable
  spelling.
- `.RemoveChild(childPath)` lowers to a `RemovedGameObjects` entry; also top-level on the instance handle.

**First-author selector→key bootstrap (load-bearing).** A nested override authored by hand
(`.On(sel => sel.Turret.Barrel, …)`) has **no sidecar `SubKey` yet** — the typed selector, resolved
through the façade manifest (spec 25) to a child path + the sub-object's durable prefab-internal id, is the
only identity information available. So the direction is asymmetric by lifecycle: on the FIRST build/save
of a new authored nested target, the adapter resolves that manifest-derived child path → the live
sub-object (walk the name path from the instance root) → its `(TargetPrefabId, TargetObjectId)` pair-key,
then **persists that key to the sidecar**; from then on the key is authoritative and the selector/path are
re-derived (never re-resolved from source). A selector that resolves to no live sub-object at first author
is a **located conflict** (§7) naming the instance + path — never a silent drop, never a guessed key. (This
is the one place the address is read *into* a key; the "address re-derived from key, never the reverse"
rule holds for every already-mapped override. The typed selector itself is compiler-checked against the
façade, so a stale accessor fails to compile before it can ever reach this path — spec 25's safety net.)

## IdentityMap / sidecar changes

- **No new top-level section.** The instance's `Kind = "PrefabInstance"` entry already carries
  `Overrides : InstanceOverrideRecord[]` (M10). Each record now persists the **durable sub-object
  pair-key** (`SubKey : PrefabInstanceKey`) + `ComponentType` + `Kind` + (property records)
  `PropertyPath`/`BaseValue`. This is what keeps a nested target addressable across reload with **no
  scene-local fileID** and **no GUID in the C# source** — the `ChildPath` in source is re-derived from
  this key, never the reverse.
- Added child GameObjects (`AddedGameObjects`) are genuine new scene objects: on first save each
  receives its own `GlobalObjectId`, recorded to `Entries[]` like any M1/M3 object (create-with-payload,
  §13 — an added child may itself carry components/refs, appended in the same Reconcile pass).
- `RemovedGameObjects` / removed components persist only their source `SubKey` — no live object exists.
- The asset cache (`Assets[]`) already covers the source-prefab GUID (M6) and any asset an override value
  references (M10); nested prefab source GUIDs referenced by `GetCorrespondingObjectFromSourceAtPath`
  join it (merge-only, never pruned, §4).

## Core test plan (RED behaviors — headless, no Unity)

1. `NestedOverride_LowersToPairKey_NotNamePath` — a mod on `Turret/Barrel`'s `Light.intensity` lowers to
   a `PropertyOverride` with the child's `SubKey`/`ComponentType == "UnityEngine.Light"`; two overrides
   equal in `(SubKey, ComponentType, PropertyPath)` but differing in `ChildPath` compare EQUAL (identity
   ignores the name path). *(Guards the spec-16 hazard.)*
2. `NestedOverride_Rename_ReDerivesPath_KeepsKey` — snapshot before/after a child rename: `SubKey`
   unchanged, `ChildPath` changed; Reconcile emits no override drop/re-add; round-trip identity-stable.
3. `NestedInNestedPrefab_Override_RoundTripsBytes` — an override on an object inside a nested prefab
   round-trips (model → Plan → simulated snapshot → Reconcile → identical model), byte-stable.
4. `NestedAddedComponent_RoundTrips` — added component on a nested child ↔ `AddedComponent{SubKey=child}`;
   materialize plan contains the add on the child; reconcile appends
   `.On(sel => sel.Turret.Barrel, x => x.AddComponent<T>())`.
5. `NestedRemovedComponent_RoundTrips` — source component removed on a nested child ↔ `RemovedComponents`
   with the child `SubKey`; round-trips as `.On(sel => sel.Turret.Wheel, x => x.RemoveComponent<T>())`.
6. `AddedChildGameObject_RoundTrips` — a non-prefab child under the instance ↔ `AddedGameObjects` node
   with `Parent.SubKey` = parent key; materialize creates it under that parent (no re-instantiate);
   reconcile appends `.AddChild("Turret", "MuzzleFlash", …)`.
7. `RemovedChildGameObject_RoundTrips` — source child stripped ↔ `RemovedGameObjects`; round-trips as
   `.RemoveChild("Turret/Antenna")`.
8. `InstanceAdd_NonPrefabChild_LowersToAddedGameObject_NotChildren` — a `GameObjectNode` authored under an
   `InstanceHandle` (not itself an instance) lands in the instance's `AddedGameObjects`, and its Reconcile
   append is an `.AddChild(...)`, NOT a top-level statement in `Children`. *(The M6-reconciliation
   decision, behavior #8.)*
9. `InstanceAdd_NestedInstanceChild_StaysNestedInstance` — a `scene.Instance(...)` under an instance
   remains a nested instance node, unaffected by #8.
10. `MaterializeReverts_NestedOverride_And_AddedChild` — desired model lacks a nested override / added
    child the live snapshot still carries → Plan contains a **revert** op (not a set-to-default / not a
    destroy-recreate); after execute the property/child is no longer a bold override.
11. `FullLoop_NestedOverrides_And_ChildEdits_RoundTrip` — one test owns model → Plan → simulated nested
    snapshot → Reconcile → identical model, covering nested property + added/removed component + added/
    removed child together.
12. `Determinism_NestedCollections_OrderingIsPairKeyed` — shuffled-input serializations of the same
    nested override set are byte-identical; ordering keys on `SubKey`/`ComponentType`/`PropertyPath`,
    never `ChildPath`.
13. `Sidecar_PersistsSubKey_ReDerivesChildPath` — `InstanceOverrideRecord` round-trips `SubKey` +
    `ComponentType`; a reload with a renamed child still matches the override by `SubKey` and re-derives
    the new `ChildPath`.
14. `CreateWithPayload_AddedChildWithComponent_ConvergesInOnePass` — an added child carrying a component
    appends the `.AddChild` statement and its component in one Reconcile; a second Reconcile is a no-op
    (§13).
15. `Reconcile_TwoEditsSameSubObject_GroupUnderOneOn` — a snapshot whose instance carries TWO nested edits
    on the SAME sub-object (e.g. an override on `Turret/Barrel`'s `Light.intensity` AND an added
    `AudioSource` on `Turret/Barrel`) emits a SINGLE typed
    `.On(sel => sel.Turret.Barrel, x => x.Override(…).AddComponent<AudioSource>())`
    block — NOT two separate `.On(sel => sel.Turret.Barrel, …)` calls. Reconciling a second edit onto a
    sub-object that ALREADY has an `.On` block in source (typed or string fallback) appends the op inside
    the existing closure (find-or-create, matched on the selector's manifest-resolved target, not literal
    text), leaving exactly one `.On` for that target. *(Guards the closure-merge behavior, behavior #10.)*

## Unity confirmation checklist (⇒ EditMode tests in `unity-gate/Assets/GateTests/`)

Each step is a real editor action against a live scene with a **multi-level** prefab (a Tank prefab
containing a nested Turret prefab, itself containing a Barrel with a `Light`).

1. Instantiate `Tank.prefab`; in the Inspector, override `Barrel`'s `Light.intensity` (a **nested**
   target, shown bold). Sync. **Expected:** the instance's source statement gains
   `.On(sel => sel.Turret.Barrel, b => b.Override(x => x.Set((Light l) => l.intensity, …)))` (the typed
   selector, spec 25); the sidecar records the override under the Barrel's `SubKey` (no GUID/fileID in the
   `.cs`).
2. Author that same nested typed `.On(sel => sel.Turret.Barrel, …)` override in code, Build. **Expected:**
   the live Barrel shows the bold override; the instance and every sub-object keep their `GlobalObjectId`
   (no re-instantiate).
3. **Rename** the `Turret` child so the façade regenerates (spec 25), then Sync. **Expected:** the typed
   selector's accessor auto-rewrites via spec 25's reference-patch — `.On(sel => sel.Turret.Barrel, …)`
   becomes `.On(sel => sel.Cannon.Barrel, …)` — and the builder still compiles; the override is NOT dropped
   and re-added — the sidecar `SubKey` is unchanged and the `Light` stays overridden.
4. Add a `Light` component to the **nested** `Barrel` (Add Component on the child), Sync. **Expected:**
   source gains `.On(sel => sel.Turret.Barrel, x => x.AddComponent<Light>())` (or, if the Barrel already
   has an `.On` block, the `.AddComponent<Light>()` op is appended INSIDE that existing block — one `.On`
   per target); a subsequent Build applies it to the same child; Sync converges (a second Sync is a no-op).
5. Drag a **new** empty child GameObject under the instance's `Turret` in the Hierarchy, Sync.
   **Expected:** source gains `.AddChild("Turret", "<name>", …)` (an `AddedGameObjects` override — NOT a
   top-level `scene.Add`); it compiles; Build re-creates it under the same parent without re-instantiating
   the Tank.
6. Delete a **source** child of the instance (Inspector → the child shows as a removed GameObject),
   Sync. **Expected:** source gains `.RemoveChild("Turret/Antenna")`; Build re-applies the removal;
   round-trip stable.
7. **Pair-uniqueness gate (open-risk).** In a Tank whose Turret prefab is instantiated **twice** inside
   it (nesting depth ≥ 2, two sub-objects reachable only through different nested prefabs), assert that
   each override target resolves to a **distinct** `(TargetPrefabId, TargetObjectId)` pair, and that
   overriding a property on one does not bleed onto the other. **Expected:** the bare pair is unique at
   depth ≥ 2; if the assertion fails, the adapter's `GetCorrespondingObjectFromSourceAtPath` +
   instance-scoped-query fallback still resolves each target correctly (the test must exercise the
   fallback, not skip). *(This test MUST exist and pass before Core relies on bare-pair matching.)*
8. Right-click the nested override → **Revert**, Sync. **Expected:** the reverted op is dropped from
   inside the `.On(sel => sel.Turret.Barrel, …)` block; if it was that block's last op, the whole
   `.On(...)` call is dropped from source; no stale call remains (M10 behavior #8, nested).

## Dependencies

- **M10** (`specs/11-m10-prefab-overrides.md`) — the root-target override model, `PropertyOverride` /
  `OverrideTarget` / `AddedComponent`, materialize-revert, stale-override conflict, deterministic
  serialization, and the `PrefabInstanceProbe` / `SceneSnapshotReader` override read path this milestone
  extends. Hard dependency.
- **Spec 25 — Typed prefab façades (`specs/25-typed-prefab-facades.md`), SHIPPED. HARD dependency.** The
  typed addressing surface this milestone consumes: `scene.Instance(Prefabs.X)` and the typed selector
  overload `.On(sel => sel.A.B, closure)` (`InstanceHandle<TRef>.On<TNode>`), the `FacadeCatalog` manifest
  + parser resolution (`BuilderParser.Facade.cs`: selector member-chain → child path + durable `LocalId`),
  the byte-stable façade generator, and the `FacadeReferencePatcher` rename auto-rewrite. Spec 25 lowers
  the typed and string `.On` forms IDENTICALLY, so the durable-pair-key identity below is unchanged — the
  typed selector is only how the address is WRITTEN and compiler-checked. This milestone owns the deep
  nested-override materialize/reconcile/revert semantics on top of that addressing surface.
- **Spec 26 — CodeScenes.Analyzers toolkit (`specs/26-codescenes-analyzers-toolkit.md`), SHIPPED. HARD
  dependency.** The source-generator framework + `<AdditionalFiles>` injection that spec 25's
  `PrefabFacadeGenerator` and façade manifest ride on, plus the `SB1103` (`.On("path")`) analyzer nudge
  toward the typed selector. Consumed transitively via spec 25.
- **M6** (`specs/completed/07-m6-prefab-instances.md`) — `PrefabInstanceNode`, `PrefabInstanceKey` (the
  pair-key reused as the durable sub-object identity), instance detection, and the `instance.Add`→
  `Children` semantics this milestone deliberately re-routes (behavior #8).
- **Spec 16** (`specs/completed/16-duplicate-sibling-identity.md`) — the identity principle the
  addressing scheme obeys: name-path-as-identity is a data-loss hazard, so the durable key is the
  pair-key in the sidecar and the name path is re-derived, never authoritative. Sub-prefab objects have
  no `var`/`.Id(...)` escape hatch, so the spec-16 heal is unavailable to them — which is exactly why
  the pair-key (not a LogicalId/structural fingerprint) is the primary key here.
- **M3 / M4 / M5 / M8 / M9** — component add/remove + `.Set` authoring, asset refs, cross-object refs,
  UnityEvents, `[SerializeReference]` — reused verbatim by nested override values and added-child payloads.

## Risks/notes

- **Rejected: name-path-as-identity.** A `"Turret/Barrel"` path (whether written as a string or re-derived
  from a typed selector) is duplicate-blind and reorder/rename fragile — the spec-16 data-loss shape. It is
  display + authoring convenience only; identity is the `(SubKey, ComponentType)` pair. Every
  ordering/equality/lookup keys on the pair, never the path. The typed selector (spec 25) is
  compiler-checked and disambiguates duplicate siblings at codegen, but it too resolves TO the durable id,
  never becomes it.
- **Rejected: LogicalId / structural-fingerprint as the primary sub-object key.** Its Name tier is
  duplicate-blind (spec-16 defect 1) and, critically, sub-prefab objects have **no** `var`/`.Id(...)`
  handle a user can attach — so the spec-16 self-heal (inject a handle) is impossible for them. The
  durable `(TargetPrefabId, TargetObjectId)` pair from `GlobalObjectId` is the only rename/dup-safe
  handle available for an object the user never authored.
- **OPEN RISK — depth ≥ 2 pair uniqueness is UNDOCUMENTED.** Whether the bare `(targetPrefabId,
  targetObjectId)` pair is globally unique for objects reachable only through two levels of nested
  prefab is not documented by Unity. The milestone therefore REQUIRES the depth-≥2 EditMode test
  (Unity confirmation #7) to assert uniqueness before Core relies on it, and mandates the
  `GetCorrespondingObjectFromSourceAtPath` + instance-scoped-query correspondence chain as the
  authoritative fallback. Core must not silently assume the pair suffices at depth.
- **`instance.Add` reconciliation touches shipped M6 behavior — handle carefully.** M6 shipped
  `instance.Add(...)` mapping to plain `Children`. Routing a non-prefab child of an instance to
  `AddedGameObjects` is a deliberate, spelled-out semantic change (behavior #8); its regression test
  (#8/#9) must pin both the new lowering AND that a nested `scene.Instance(...)` child is unaffected, so
  the change does not silently alter existing whole-prefab-instance authoring.
- **Performance:** the read side enumerates inside the instance only to resolve override target
  pair-keys (bounded by the number of overrides + the source→instance correspondence map), not to emit
  sub-objects as nodes. The instance stays one `SnapshotNode`; do not regress toward a full per-keystroke
  descent of every instance (the M6/CLAUDE.md sync-performance constraint).
- **`GetRemovedGameObjects`** is 2022.2+ and present in the project's 6000.5 — used directly, not gated;
  only flag-as-unsupported if a future target lacks it (§7), never silently drop.
- **Closure-merge Roslyn complexity (scoped `.On` emission).** The scoped authoring shape means a
  sub-object's nested edits all live inside ONE `.On(sel => sel.<Child>, x => x.op1().op2())` block, so
  Reconcile's SourceEdit machinery must FIND-OR-CREATE that closure — locate an existing `.On` invocation
  whose selector RESOLVES to the same sub-object (via the façade manifest, spec 25 — typed selector or a
  string-path fallback), descend into its closure lambda body, and append the op there, creating a new
  `.On` only when none exists — and on revert must remove an op from inside the closure and drop the `.On`
  call when its last op goes. This is strictly more Roslyn work than M10's flat statement-appends (which
  never had to reach inside an existing call's lambda). Getting the find-or-create match keyed on the
  resolved durable child (not on the selector's literal text or span order) is load-bearing: a miss
  re-emits a duplicate `.On` for the same target, which behavior #10 and test #15 forbid.
- **Value vs objectReference** on a nested `PropertyModification` stays mutually exclusive per entry
  (M10 invariant), unchanged.
