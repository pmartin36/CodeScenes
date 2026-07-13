# M10 — Prefab-instance override round-trip (both directions)

### Additions to the contract

M10 introduces the following types. They extend the `PrefabInstanceNode` established by M6; all
value payloads reuse §3 `ValueNode`, `AssetRef`, `ObjectRef`, `ComponentData`, and `GameObjectNode`
verbatim — no parallel value model is invented.

```
PrefabInstanceNode                              // M6 type; M10 adds the four override collections
  ...M6 fields (LogicalId, SourcePrefab: AssetRef, Transform, Name, ...)
  Overrides         : PropertyOverride[]        // ordered; per-property modifications
  AddedComponents   : AddedComponent[]          // components added on the instance, absent in source
  RemovedComponents : OverrideTarget[]          // source components stripped on the instance
  AddedGameObjects  : GameObjectNode[]          // new child GOs added under the instance
  RemovedGameObjects: OverrideTarget[]          // source child GOs stripped on the instance

OverrideTarget                                  // the pair key — see §4; NOT a single fileID
  PrefabId          : string                    // GUID:fileID of the source prefab the object lives in
                                                //   (disambiguates nested prefabs)
  ObjectId          : long                      // local fileID of the target object WITHIN that prefab

PropertyOverride                                // mirrors Unity PropertyModification EXACTLY
  Target            : OverrideTarget            // the object inside the source prefab being modified
  PropertyPath      : string                    // serialized propertyPath, e.g. "m_LocalPosition.x", "_health"
  Value             : ValueNode                 // Primitive/Enum/Vec*/Color for the value field
  ObjectReference   : ValueNode?                // AssetRef or ObjectRef when the modification is a ref;
                                                //   null otherwise. Exactly one of Value/ObjectReference
                                                //   is meaningful, matching PropertyModification semantics.

AddedComponent
  Target            : OverrideTarget            // the instance GO (inside the prefab) the component hangs on
  Component         : ComponentData             // §3 ComponentData, full field set
```

Every override keys on the `(PrefabId, ObjectId)` PAIR. An object inside a prefab instance has **no
own local fileID** in the scene, so a single fileID cannot address it (foundation §4: prefab-instance
objects are keyed on the `(targetPrefabId, targetObjectId)` pair). `OverrideTarget` is that pair.

---

## Goal
Represent, materialize, and reconcile the per-instance override layer of a prefab instance — the
`m_Modification.m_Modifications` list plus added/removed components and GameObjects — so a user can
author instance tweaks in code and edits made on an instance in Unity round-trip back to code.

## In scope
- Model `m_Modification.m_Modifications` as `PrefabInstanceNode.Overrides` (`PropertyOverride[]`).
- Model added components / removed components / added GameObjects / removed GameObjects on an instance.
- Key every override on the `(PrefabId, ObjectId)` pair via `OverrideTarget`.
- **Materialize** (code→scene): apply the authored override set onto an instantiated prefab instance.
- **Reconcile** (scene→code): detect a user-made override / add / remove on an instance and patch source.
- Conflict detection when the source prefab asset changed a property that also carries a stale override.
- Canonical serialization of the override collections (deterministic ordering by target then path).

## Out of scope
- Whole-prefab instantiation, presence detection, source-prefab GUID resolution — owned by **M6**.
- Nested-prefab *authoring* (creating prefab-in-prefab structures); M10 only *addresses* nested
  targets via `PrefabId` so overrides on them round-trip. Structural nested-prefab editing → deferred.
- Variant prefabs as a distinct authored concept (a variant's own mods are still `PropertyModification`s
  and covered, but "author a new variant asset" is not).
- Reverting the entire instance / "apply all to source" bulk operations as authoring verbs.
- UnityEvent / `[SerializeReference]` values inside overrides beyond what M8/M9 already model; an
  override whose value is such a field reuses those models, it is not re-specified here.

## Core deliverables

### Types added/changed (referencing §3)
- `PrefabInstanceNode.Overrides / AddedComponents / RemovedComponents / AddedGameObjects /
  RemovedGameObjects` (above). `PropertyOverride`, `OverrideTarget`, `AddedComponent` (above).
- No change to `SceneModel`, `GameObjectNode`, `ComponentData`, `ValueNode`, `AssetRef`, `ObjectRef`.
  `PropertyOverride.Value` / `ObjectReference` are `ValueNode`s; a reference modification serializes as
  `AssetRef` (project asset) or `ObjectRef` (target elsewhere in this scene), per §3.

### Functions / behaviors (testable contracts)
1. **Override model ↔ `m_Modifications`.** Given a snapshot list of
   `PropertyModification{target, propertyPath, value, objectReference}`, the reader produces
   `PropertyOverride[]` where each `target` lowers to an `OverrideTarget(PrefabId, ObjectId)`, and the
   writer produces the identical `PropertyModification` list back (round-trip is byte-stable after
   canonicalization). `PropertyModification` has **exactly** these four members — no others are emitted.
2. **Pair-key mapping.** Two overrides that share a `PropertyPath` but target different objects inside
   the prefab (different `ObjectId`), or the same object reached through different nested prefabs
   (different `PrefabId`), are distinct entries and never collapse. A lookup keyed on `ObjectId` alone
   MUST NOT match across differing `PrefabId`.
3. **Added component.** A component present on the instance but absent from the source prefab lowers to
   an `AddedComponent{Target, Component:ComponentData}`; materialize adds it; reconcile appends
   `.AddComponent<T>()` to the instance's source statement.
4. **Removed component.** A source component stripped on the instance lowers to a `RemovedComponents`
   entry (`OverrideTarget`); round-trips as a `.RemoveComponent<T>()` (or equivalent) authoring call.
5. **Added / removed GameObject.** A child GO added under the instance lowers to an `AddedGameObjects`
   `GameObjectNode`; a stripped source child lowers to a `RemovedGameObjects` `OverrideTarget`. Both
   round-trip.
6. **Materialize applies overrides.** `Materialize` on a `PrefabInstanceNode` with overrides produces
   `Plan` ops that, after M6 instantiation, set each modified property, add/remove the listed
   components, and add/remove the listed GameObjects — **in place**, preserving the instance's
   `GlobalObjectId` (never re-instantiate to apply overrides).
7. **Reconcile detects instance edits.** Given a `SceneSnapshot` whose prefab instance carries mods /
   adds / removes not present in the parsed `SceneModel`, `Reconcile` emits a `SourcePatch` adding the
   corresponding `.Override(...)` / `.AddComponent<T>()` / `.RemoveComponent<T>()` / child edits to the
   instance's source statement, span-local.
8. **Revert drops the override.** If the snapshot no longer carries an override that the `SceneModel`
   authored (user reverted it in Unity), `Reconcile` emits a patch removing that `.Override(...)` /
   added-component call. Reverting to the prefab default is the delete of the entry, not a value edit.
9. **Stale-override conflict.** If the source prefab asset's default for a property changed such that
   an existing authored override now refers to a base state that no longer exists (the recorded base
   value the override was written against no longer matches the prefab default, and the instance value
   equals the *new* default), the override is **stale**: Core surfaces a **conflict** (per §7) naming
   `instance > target > propertyPath`, and does not silently keep or drop it.
10. **Deterministic ordering.** Canonical serialization orders `Overrides` by `(PrefabId, ObjectId,
    PropertyPath)`, `AddedComponents` by `(Target, Type.FullName)`, and the GO collections by target,
    so repeated round-trips are stable (§8 determinism).

## Editor adapter deliverables
- **Read** the override layer from a live instance into snapshot form:
  `PrefabUtility.GetObjectOverrides` → property mods; `PrefabUtility.GetAddedComponents`;
  `PrefabUtility.GetRemovedComponents`; `PrefabUtility.GetAddedGameObjects`; and
  `PrefabUtility.GetRemovedGameObjects` (Unity 2022+; if unavailable, removed-GO detection is flagged
  as unsupported rather than silently dropped). Each modification's `target` is resolved to a
  `GlobalObjectId` and lowered to the `(PrefabId, ObjectId)` pair.
- **Write / execute** override `Plan` ops: set property mods via `SerializedObject` on the instance
  (Unity records them into `m_Modification.m_Modifications`); add components with
  `PrefabUtility.` add-component flow; remove with `PrefabUtility.` remove-component; add child GOs
  under the instance; revert an override via the appropriate `PrefabUtility` revert call.
- **Resolve** `target` object ↔ `(PrefabId, ObjectId)` pair, using `GlobalObjectId` and the source
  prefab GUID from M6.
- No override *logic* lives here — the adapter only reads the four/five lists and executes the Plan.

## Authoring API added
Fluent surface on a prefab instance handle (M6 introduced `scene.Instance(path)`):

```csharp
scene.Instance("Assets/Prefabs/Enemy.prefab")
     .Override(e => e.Set(x => x.health, 50))   // per-property modification on a source object
     .AddComponent<Light>();                    // added component on the instance
// also: .RemoveComponent<T>(), .Override(child => child.Set(...)) for nested targets
```

- `.Override(...)` closures use the same typed `.Set(path, value)` / `.Set(expr, value)` surface as
  M3, lowering to `PropertyOverride` entries.
- `.AddComponent<T>(cfg)` lowers to `AddedComponent`; `.RemoveComponent<T>()` lowers to a
  `RemovedComponents` entry.

## IdentityMap / sidecar changes
- No new top-level sidecar section. Prefab-instance entries already exist (M6). M10 adds, per instance
  entry, the resolution needed for override targets: each `OverrideTarget` records `PrefabId`
  (GUID:fileID of the source prefab object's owning prefab) and `ObjectId`. These persist alongside
  the instance entry so a target that has no scene-local fileID stays addressable across reload.
- `AddedComponents` / `AddedGameObjects` are genuine new scene objects: on first save they receive
  their own `GlobalObjectId`, recorded to `Entries[]` like any M1/M3 object.
- The asset cache (`Assets[]`) covers the source-prefab GUID (already from M6) and any asset referenced
  by an override value.

## Core test plan (RED behaviors)
1. `PropertyModification` list (with `target`, `propertyPath`, `value`, `objectReference`) reads into
   `PropertyOverride[]` and writes back to a byte-identical list after canonicalization.
2. `objectReference`-type modification lowers to `AssetRef` (asset) and `ObjectRef` (in-scene) and back.
3. Pair-key: same `PropertyPath`, different `ObjectId` → two entries; different `PrefabId`, same
   `ObjectId` → two entries; `ObjectId`-only lookup does not cross `PrefabId`.
4. Added component on instance ↔ `AddedComponent`; materialize plan contains the add; reconcile patch
   appends `.AddComponent<T>()`.
5. Removed component ↔ `RemovedComponents`; round-trips as `.RemoveComponent<T>()`.
6. Added child GO ↔ `AddedGameObjects`; removed source child ↔ `RemovedGameObjects`; both round-trip.
7. Full round-trip: model → Plan → (simulated) snapshot with overrides → Reconcile → identical model.
8. Materialize preserves instance identity: plan applies overrides in place, emits no re-instantiate op.
9. Revert: snapshot missing a previously-authored override → Reconcile emits an override-removal patch.
10. Stale-override conflict: prefab default changed under an authored override + instance value equals
    the new default → Core emits a located conflict, neither dropping nor keeping silently.
11. Determinism: two serializations of the same override set (input order shuffled) are identical.

## Unity confirmation checklist
1. Instantiate `Enemy.prefab` in a scene via the builder; select the instance; in the Inspector
   override a property (e.g. set `health` to 50 — shown bold/overridden). Run Reconcile → the instance's
   source statement gains `.Override(e => e.Set(x => x.health, 50))`. **Expected:** override captured.
2. Add a `Light` component to the instance (Add Component). Run Reconcile → source gains
   `.AddComponent<Light>()`. **Expected:** added component reflected.
3. Remove a component that came from the prefab (Inspector → Removed Component). Run Reconcile → source
   gains `.RemoveComponent<T>()`. **Expected:** removal reflected.
4. Right-click the overridden property → **Revert**. Run Reconcile → the `.Override(...)` call is
   dropped from source. **Expected:** override gone, no stale call left.
5. Author `.Override(e => e.Set(x => x.health, 50))` in code, Materialize → the live instance shows
   `health = 50` as a bold override (not a broken/duplicated instance; same `GlobalObjectId`).
   **Expected:** override applied in place.
6. Change the property's default in `Enemy.prefab` itself while a stale `.Override` targets it →
   Materialize/Reconcile surfaces a located conflict naming the instance, target, and propertyPath.
   **Expected:** conflict surfaced, nothing silently reconciled.

## Dependencies
- **M6** — whole-prefab instances (`scene.Instance`, source-prefab GUID resolution, presence detection).
- **M3** — component add/remove and `.Set` field authoring (reused by `.Override` and `.AddComponent`).
- **M4** — asset refs (override values that reference assets).
- **M5** — `ObjectRef` (override values that reference in-scene objects).

## Risks/notes
- Nested prefabs make the `PrefabId` half of the key load-bearing; addressing-only support is in scope,
  structural nested editing is not.
- `GetRemovedGameObjects` availability varies by Unity version; when absent, removed-GO detection is
  flagged unsupported (never silently dropped), per §7.
- `PropertyModification` value vs `objectReference` are mutually exclusive per entry; the model must not
  populate both — tests assert exactly one is meaningful.
- Stale-override detection needs a recorded base value to compare against the prefab's current default;
  that base is what distinguishes "user changed override" from "prefab default drifted."
