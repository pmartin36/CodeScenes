# M10 — Prefab-instance override round-trip (both directions)

> ## v0 scope decisions (banked 2026-07-21 — authoritative, override the body below where they conflict)
> This spec describes the full override vision; **this pass ships a bounded v0**:
> 1. **ROOT-ONLY this pass.** Overrides target the instance **root's own components** only. Addressing an
>    object **inside** the prefab (a nested child / component below the root) is **deferred to a separate
>    milestone `M-nested-props`** (flagged critical), written after a focused grounding pass on Unity's
>    nested-prefab target addressing + a rename/dup-sibling-safe handle for objects the user never authored
>    (reuse the LogicalId/structural-fingerprint model, spec 16). Until then, nested-target overrides are
>    **read as opaque and preserved/flagged** (the current M6 behavior), never dropped (§7).
> 2. **`PropertyOverride.Value` fidelity: one override per Unity `PropertyModification`.** Unity stores each
>    modification's value as a **string**, and a structured value (e.g. a Vec3) is **three** separate mods
>    (`.x/.y/.z`). So a `PropertyOverride` maps 1:1 to a single Unity mod (scalar/string value) — drop any
>    reading of the type table that implies a single `PropertyOverride` carries a whole `Vec3`/`Color`.
> 3. **Root name + transform are NOT `.Override(...)` calls.** They are already first-class fields on the
>    M6 `PrefabInstanceNode` (`Name`, `Transform`), so those edits round-trip through **those** fields —
>    overriding them is fully supported, just not double-encoded as an override. Unity bookkeeping mods
>    (`m_RootOrder` / sibling index — handled by structural reorder sync) are **skipped**.
> 4. **Stale-override detection needs a recorded base value** the current sidecar section omits: persist,
>    per authored override, the prefab-default value it was written against, so drift ("prefab default
>    changed under the override") is detectable per behavior #9. Add this to the sidecar (see §IdentityMap).
> 5. **Added/Removed child-GameObject overrides — DEFERRED (out of scope this pass).** Property overrides
>    and added/removed **components** on the root round-trip fully; adding or removing a child **GameObject**
>    on the instance (`AddedGameObjects`/`RemovedGameObjects`, behavior #5, tests #6/#7) is **not** authored
>    or round-tripped in v0 — it has no authoring surface yet and would collide with M6's
>    `instance.Add`→`Children` semantics, which is its own deliberate design. Such edits stay
>    **read-as-opaque-and-preserved** (M6 behavior), never dropped (§7). Deferred to the same follow-up as
>    nested addressing. (The four override collections may still exist on the model for forward-compat, but
>    only property overrides + added/removed components are authored/materialized/reconciled this pass.)
> 6. **Materialize must REVERT, not just set** (fixes an omission in behavior #6). When the desired (code)
>    model lacks a property override the live instance still carries (the user deleted a `.Override(...)`),
>    Materialize emits a **revert-override** Plan op executed via the appropriate `PrefabUtility` revert
>    call — NOT a `SetInstanceOverride` to the default value (which leaves a bold override in Unity).
>    Symmetric with Reconcile's behavior #8. Applies to overridden properties and added/removed components.

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
- Model + round-trip added components / removed components on the root instance. (Added/removed
  **GameObjects** are modelled for forward-compat but DEFERRED — not authored/round-tripped this pass; banner #5.)
- Key every override on the `(PrefabId, ObjectId)` pair via `OverrideTarget`.
- **Materialize** (code→scene): apply the authored override set onto an instantiated prefab instance.
- **Reconcile** (scene→code): detect a user-made override / add / remove on an instance and patch source.
- Conflict detection when the source prefab asset changed a property that also carries a stale override.
- Canonical serialization of the override collections (deterministic ordering by target then path).

## Out of scope
- Whole-prefab instantiation, presence detection, source-prefab GUID resolution — owned by **M6**.
- **Nested-target overrides — DEFERRED to `M-nested-props` (out of scope this pass).** Any override,
  add, or remove targeting an object BELOW the instance root (a nested child / component, including one
  inside a nested prefab) is not modelled or round-tripped here; it stays **read-as-opaque-and-preserved**
  (the current M6 behavior), never dropped (§7). This pass round-trips **ROOT-target** overrides only.
  Nested-prefab *authoring* (creating prefab-in-prefab structures) is likewise out of scope.
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
2. **Pair-key mapping (root scope).** Two separate scene INSTANCES of the same prefab share the same
   `ObjectId` (the prefab root's source-object fileID) but differ in `PrefabId` (per-instance); their
   root overrides are distinct entries and never collapse. A lookup keyed on `ObjectId` alone MUST NOT
   match across differing `PrefabId`. (Distinguishing different objects *inside* one prefab, or the same
   object reached through different nested prefabs, is nested scope → deferred to `M-nested-props`.)
3. **Added component.** A component present on the instance but absent from the source prefab lowers to
   an `AddedComponent{Target, Component:ComponentData}`; materialize adds it; reconcile appends
   `.AddComponent<T>()` to the instance's source statement.
4. **Removed component.** A source component stripped on the instance lowers to a `RemovedComponents`
   entry (`OverrideTarget`); round-trips as a `.RemoveComponent<T>()` (or equivalent) authoring call.
5. **Added / removed GameObject — DEFERRED (banner #5).** These collections are modelled, but the
   authoring surface, materialize, and reconcile of child-GameObject overrides are out of scope this pass;
   such edits are read-as-opaque-and-preserved (M6), never dropped. No added-/removed-child authoring verb
   is introduced here.
6. **Materialize applies AND reverts.** `Materialize` on a `PrefabInstanceNode` produces `Plan` ops that,
   after M6 instantiation, set each authored property override and add/remove the listed components —
   **in place**, preserving the instance's `GlobalObjectId` (never re-instantiate). Crucially, a property
   override or added/removed component present on the LIVE instance but ABSENT from the desired model is
   **reverted** via the appropriate `PrefabUtility` revert op (banner #6) — never left as a stale bold
   override. (Child-GameObject add/remove is deferred, banner #5.)
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
3. Pair-key (root scope): two instances of the same prefab (same `ObjectId`, different `PrefabId`) →
   two distinct entries; `ObjectId`-only lookup does not cross `PrefabId`.
4. Added component on instance ↔ `AddedComponent`; materialize plan contains the add; reconcile patch
   appends `.AddComponent<T>()`.
5. Removed component ↔ `RemovedComponents`; round-trips as `.RemoveComponent<T>()`.
6. **Materialize revert (banner #6):** desired model lacks a property override / added component the live
   snapshot still carries → Plan contains a **revert** op (a `PrefabUtility` revert), NOT a set-to-default;
   after execute, the property is no longer a bold override. (Added/removed child-GameObject round-trip is
   DEFERRED, banner #5 — not authored/tested this pass.)
7. Full round-trip (property overrides + added/removed components; excludes child-GameObjects): model →
   Plan → (simulated) snapshot with overrides → Reconcile → identical model. One test owns the whole loop.
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
- Nested prefabs make the `PrefabId` half of the key load-bearing even at root scope (it distinguishes
  two instances of the same prefab). Nested-*target* addressing (objects below the root) is deferred to
  `M-nested-props`; this pass round-trips root-target overrides only.
- `GetRemovedGameObjects` availability varies by Unity version; when absent, removed-GO detection is
  flagged unsupported (never silently dropped), per §7.
- `PropertyModification` value vs `objectReference` are mutually exclusive per entry; the model must not
  populate both — tests assert exactly one is meaningful.
- Stale-override detection needs a recorded base value to compare against the prefab's current default;
  that base is what distinguishes "user changed override" from "prefab default drifted."
