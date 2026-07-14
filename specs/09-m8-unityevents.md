# M8 — UnityEvents / OnClick wiring

### Additions to the contract
This milestone introduces new types/ops not in `00-foundation.md`. They are flagged here per §3's rule.

- **`ValueNode.UnityEventListeners`** — new `ValueNode` variant (§3):
  ```
  UnityEventListeners(listeners: UnityEventListener[])   // ordered; empty list == cleared event

  UnityEventListener
    Target     : ValueNode      // MUST be ObjectRef (scene target, §3/M5) or AssetRef (asset target, §3/M4); null Target allowed (dangling call)
    MethodName : string         // Unity m_MethodName; the setter or method invoked
    CallState  : "Off" | "RuntimeOnly" | "EditorAndRuntime"   // == UnityEventCallState; default "RuntimeOnly"
    ArgMode    : "void" | "int" | "float" | "string" | "bool" | "object" | "dynamic"   // == PersistentListenerMode; "dynamic"==EventDefined (m_Mode 0): method receives the event's OWN runtime arg(s)
    ArgValue   : ValueNode?      // present iff ArgMode is a static value mode (int/float/string/bool/object); void AND dynamic carry no ArgValue:
                                 //   int→Primitive(int), float→Primitive(float), string→Primitive(string),
                                 //   bool→Primitive(bool), object→ObjectRef | AssetRef (the m_ObjectArgument)
  ```
  A `UnityEvent`-typed serialized field (e.g. `Button.m_OnClick`) is stored in `ComponentData.Fields`
  under its serialized `propertyPath` (e.g. `"m_OnClick"`) with a `UnityEventListeners` value.
- **`Plan` op `SetUnityEvent(path, listeners)`** — new op in the §5 enumeration; replaces the full
  persistent-call list at `path` (idempotent, order-preserving). Chosen over per-call ops so the plan
  stays declarative and deterministic.

Everything else binds to `00-foundation.md` verbatim (§2 seam, §3 `ObjectRef`/`AssetRef`/`TypeRef`,
§4 identity, §5 directions, §7 conflict philosophy).

---

## Goal
Author and round-trip `UnityEvent` **persistent listeners** — the classic UI `Button.OnClick → call a
method on a target object` wiring — in both directions, including the target (scene object or asset),
the invoked method, the call state, and either a single typed static argument OR a **dynamic** binding
that forwards the event's own runtime argument(s) to the method (multi-arg `UnityEvent<T0,T1,…>`).

## In scope
- Modeling `UnityEvent` persistent listeners as `ValueNode.UnityEventListeners` on any `UnityEvent`
  (and `UnityEvent<T>` for the single-arg static modes) serialized field.
- `void` listeners (zero-arg method), **1-arg static** listeners (`int | float | string | bool |
  object`), and **dynamic** (EventDefined) listeners that forward the event's own runtime argument(s)
  to a signature-matching method — this covers multi-argument `UnityEvent<T0,T1,…>` wiring (the args
  are not stored; Unity passes them at invoke time).
- Target resolution both ways: scene-object target via `ObjectRef` (depends on M5) and project-asset
  target via `AssetRef` (depends on M4).
- `CallState` (`Off | RuntimeOnly | EditorAndRuntime`) round-trip.
- Materialize: wire/replace persistent listeners on the live component.
- Reconcile: detect a listener added / removed / target-changed / method-changed / arg-changed /
  callstate-changed in Unity and patch the source.
- Listener **ordering** preserved (Unity invokes in array order).
- Authoring surface `.OnClick(target, methodName[, arg][, callState])` plus a generic
  `.OnEvent(eventPath, target, methodName, …)` for non-Button `UnityEvent` fields.

## Out of scope
- Runtime (`AddListener` / non-persistent) listeners — not serialized, out of the tool's remit.
- More than one *static* argument is not something Unity persists at all (a persistent call stores at
  most one static arg, or uses dynamic forwarding); a static arg whose type is outside the six modes →
  `Unsupported`, flagged.
- Custom `[Serializable]` `UnityEvent` subclasses beyond field-typed discovery (handled generically if
  they serialize the standard `m_PersistentCalls` shape; exotic shapes → `Unsupported`).
- Authoring-time validation that `methodName` exists / is signature-compatible on the target type
  (deferred to a later analyzer; M8 fails loud only at Materialize when Unity rejects the wire).

## Core deliverables
### Types added/changed (referencing §3)
- Add `ValueNode.UnityEventListeners` and the `UnityEventListener` record (above).
- Add `Plan` op `SetUnityEvent(path, listeners)` (§5 op list).
- No change to `ObjectRef` / `AssetRef` / `TypeRef` — reused as the `Target` and `object`-arg carriers.

### Functions/behaviors (each a testable contract)
- **Canonical serialization** — a `UnityEventListeners` value serializes deterministically: listeners
  in array order; each listener emits `Target`, `MethodName`, `CallState`, `ArgMode`, and `ArgValue`
  (omitted when `void`). Byte-identical across repeated serialization of an equal model.
- **Model → serialized-call projection** — Core exposes a pure mapping
  `UnityEventListener ↔ persistent-call fields` (`m_Target`/`m_TargetAssemblyTypeName`, `m_MethodName`,
  `m_Mode`, `m_Arguments.{m_ObjectArgument,m_ObjectArgumentAssemblyTypeName,m_IntArgument,
  m_FloatArgument,m_StringArgument,m_BoolArgument}`, `m_CallState`) so the adapter is a dumb applier.
  Contract: mode↔`ArgMode` table (Risks) is total and reversible for all seven modes; mode `0`
  (EventDefined) maps to `ArgMode="dynamic"` (target+method preserved, no stored `ArgValue`).
- **Diff** — `Diff` treats a `UnityEvent` field as changed when the ordered listener list differs by
  any of: count, per-index `Target` (compared on `ObjectRef.targetLogicalId` / `AssetRef.Guid`+`FileId`),
  `MethodName`, `ArgMode`, `ArgValue`, or `CallState`. Emits a single `SetUnityEvent` op for the field.
- **Materialize → Plan** — a desired listener list different from actual lowers to one
  `SetUnityEvent(path, listeners)` op; identical lists produce **no** op (idempotent, no-op stability).
- **Reconcile → SourcePatch** — a listener delta observed in the snapshot patches the exact authoring
  call span: adds/removes an `.OnClick(...)`/`.OnEvent(...)` statement, or rewrites its
  target / method / arg / callstate argument in place, formatting-preserving (§5). A target that
  resolves to no known `LogicalId` and no asset `Guid` → **conflict**, surfaced, never guessed (§4/§7).
- **Target kind resolution** — Core decides `ObjectRef` vs `AssetRef` from the snapshot's persistent
  target: a target whose `GlobalObjectId` maps to an in-scene node → `ObjectRef(targetLogicalId)`; a
  target resolving to a project asset GUID → `AssetRef`; neither → conflict.
- **Fail-loud** — a listener whose `MethodName` is empty or whose `Target` is unresolved is reported
  with object+field+source/scene location (§7), not silently pruned.
- **UnityEvent on a newly-created object (§13).** A listener on a Button/object that was editor-created
  in the same edit inherits the M4/M5 create-with-payload seam: the `.OnClick(…)`/`.OnEvent(…)` call is
  appended onto that object's just-created statement in the same Reconcile pass (owner and any new listener
  target mapped in-memory via M2b's `AddedEntry`, §13 rule 1; two-pass when the target is also new), or
  reported and converged on a guaranteed second Sync (§13 rule 2) — never silently dropped. Cites §13
  (create-with-payload).

## Editor adapter deliverables
- **Read**: for each `UnityEvent` serialized field, read persistent calls via `SerializedObject` at
  `<eventPath>.m_PersistentCalls.m_Calls` (or `UnityEventBase.GetPersistentEventCount/
  GetPersistentTarget/GetPersistentMethodName/GetPersistentListenerState`), stamp each target with its
  `GlobalObjectId`, and hand raw persistent-call fields to Core's projection. Object arguments stamped
  likewise.
- **Write**: execute `SetUnityEvent` by clearing existing persistent calls and adding the desired list
  via `UnityEventTools.AddPersistentListener` / `AddStringPersistentListener` /
  `AddIntPersistentListener` / `AddFloatPersistentListener` / `AddBoolPersistentListener` /
  `AddObjectPersistentListener` (or by editing `m_PersistentCalls.m_Calls` through `SerializedObject` +
  `ApplyModifiedPropertiesWithoutUndo`), then `SetPersistentListenerState` for `CallState`. Never
  wipe-and-recreate the component (§5).
- **Resolve** target/object-arg: `GlobalObjectId` ↔ scene object (M5) and asset GUID/fileId ↔ asset
  object (M4) at the boundary.
- Adapter carries **no** mode/arg logic beyond calling the typed API Core selects — all decisions come
  from Core's projection.

## Authoring API added
```csharp
// Button convenience (m_OnClick):
scene.Add("Button").Component<Button>(b => b.OnClick(door, nameof(Door.Open)));           // void
scene.Add("Button").Component<Button>(b => b.OnClick(audio, nameof(AudioSource.Play)));    // void, asset-less scene target
scene.Add("Button").Component<Button>(b => b.OnClick(lamp, nameof(Lamp.SetLevel), 3));     // int arg
scene.Add("Button").Component<Button>(b => b.OnClick(mixer, nameof(Mixer.SetVol), 0.5f,    // float arg + call state
                                                     callState: UnityEventCallState.EditorAndRuntime));

// Generic UnityEvent field on any component:
b.OnEvent(c => c.onValueChanged, target: hud, method: nameof(Hud.Refresh));                 // bool/int/etc. per field type

// Dynamic — forward the event's own arg(s) to a matching method (covers multi-arg UnityEvent<T…>):
slider.Component<Slider>(s => s.OnEvent(x => x.onValueChanged, target: hud,
                                        method: nameof(Hud.SetValue), dynamic: true));       // ArgMode "dynamic", no stored value
```
- `target` is a builder **handle** (→ `ObjectRef`, §6) or an asset reference (→ `AssetRef`).
- Lowering (Editor→Core): `.OnClick(...)`/`.OnEvent(...)` → `ComponentData.Fields[eventPath]` =
  `UnityEventListeners([...])`; overloads select `ArgMode`/`ArgValue`; default `callState` =
  `RuntimeOnly`.

## IdentityMap / sidecar changes
- No new entry kinds. Listener **targets** reuse existing identity: scene targets resolve through the
  `Entries[]` `LogicalId ↔ GlobalObjectId` map (§4); asset targets/args resolve through `Assets[]`
  (`Guid`, `LastKnownPath`, `TypeHint`) and re-derive display paths.
- An object-mode argument that references an asset adds/refreshes an `Assets[]` entry like any
  `AssetRef` (M4 behavior, inherited).

## Core test plan (RED tests — behaviors)
- **Model ↔ serialized listener (void)**: build a `UnityEventListener{ Target=ObjectRef, Method,
  ArgMode=void, CallState=RuntimeOnly }`; project to persistent-call fields and back → equal;
  `m_Mode == 1` (Void).
- **Round-trip void**: SceneModel with `.OnClick(door, "Open")` → serialize → parse → equal model.
- **Round-trip 1-arg** (one test per mode int/float/string/bool/object): `ArgValue` and `m_Mode`
  map correctly (`3/4/5/6/2`); `m_Int/Float/String/Bool/ObjectArgument` populated; reverse equal.
- **Target = scene object** vs **target = asset**: `ObjectRef` resolves via IdentityMap; `AssetRef`
  resolves via `Assets[]`; each round-trips; each renders the correct authoring arg.
- **Listener ordering**: two-listener event preserves order through Materialize and Reconcile.
- **Listener removal**: actual has fewer/no listeners than expected → Reconcile deletes the matching
  `.OnClick(...)` statement (or empties the list) via `SourcePatch`; empty list serializes as cleared.
- **Diff idempotence**: identical desired/actual listener lists → zero `SetUnityEvent` ops.
- **CallState round-trip**: `EditorAndRuntime` / `Off` survive both directions.
- **Dynamic (EventDefined) round-trip**: an `m_Mode == 0` listener → `ArgMode="dynamic"`, no
  `ArgValue`, target+method preserved; round-trips; authoring emits the `dynamic: true` form; a
  multi-arg `UnityEvent<int,float>` wired dynamically preserves target+method through both directions.
- **Unsupported passthrough**: a persistent call with a genuinely non-standard shape (e.g. a static
  arg whose type is outside the six modes) → `ValueNode.Unsupported`, flagged, byte-identical
  round-trip; not coerced into a supported mode.
- **Conflict**: a snapshot listener whose target maps to no `LogicalId`/asset → surfaced conflict, no
  patch emitted.
- **`Reconcile_UnityEventOnNewObject_Converges`** (§13 create-with-payload): a newly editor-created
  Button/object carrying a UnityEvent listener in one edit → the `.OnClick(…)` call is appended onto that
  object's just-created statement in the same pass (owner mapped via M2b's `AddedEntry`; two-pass when
  the listener target is also new), or reported and converged on a second Sync; second Sync of the
  unchanged scene is a no-op; no silent drop.

## Unity confirmation checklist
1. Open a scene with a `Button` (`door` GameObject present, `Door.Open()` public). Author
   `.Component<Button>(b => b.OnClick(door, nameof(Door.Open)))`, Materialize → in Unity the Button's
   OnClick shows one persistent entry: target `door`, function `Door.Open ()`, RuntimeOnly. **Expected:** wired, no errors.
2. In Unity, change the OnClick target to a different GameObject → save. **Expected:** source's
   `.OnClick(...)` first argument updates to the new handle; map unchanged for other entries.
3. In Unity, change the invoked method to a 1-arg `int` method and set the value to `7` → save.
   **Expected:** source becomes `.OnClick(target, nameof(...), 7)` (ArgMode int, value 7).
4. In Unity, set the OnClick target to a **project asset** (e.g. an `AudioSource` on a prefab asset or
   an asset object arg) → save. **Expected:** source arg becomes the asset reference; `Assets[]` gains
   its GUID.
5. In Unity, set the entry's call state to **Editor And Runtime** → save. **Expected:** source gains
   `callState: EditorAndRuntime`.
6. In Unity, remove the OnClick entry → save. **Expected:** the `.OnClick(...)` statement is removed
   from source.
7. In Unity, wire a `Slider.onValueChanged` **dynamically** to a `Hud.SetValue(float)` method (drag it
   under the "Dynamic float" section) → save. **Expected:** source becomes
   `.OnEvent(x => x.onValueChanged, target: hud, method: nameof(Hud.SetValue), dynamic: true)`; round-trips.
8. Re-run Materialize with no code change. **Expected:** no plan ops (idempotent).

## Dependencies
- **M3** (components + serialized fields; `ComponentData.Fields` by `propertyPath`).
- **M5** (`ObjectRef` handle resolution; scene-object targets).
- **M4** (`AssetRef` resolve/persist; asset targets and object-mode asset args).
- **M2** (Reconcile + Roslyn `SourcePatch` machinery for statement/arg edits).

## Risks/notes
- **Mode ↔ ArgMode table** (`PersistentListenerMode`): `0 EventDefined`→`dynamic`,
  `1 Void`→`void`, `2 Object`→`object`, `3 Int`→`int`, `4 Float`→`float`, `5 String`→`string`,
  `6 Bool`→`bool`. `UnityEventCallState`: `0 Off`, `1 RuntimeOnly`, `2 EditorAndRuntime`. In dynamic
  mode the method's parameter types are those of the event's generic args (`UnityEvent<T…>`), derivable
  from the field type; Core stores only target+method and lets the adapter wire via the dynamic API.
- `m_TargetAssemblyTypeName` / `m_ObjectArgumentAssemblyTypeName` are Unity-populated; Core stores the
  method/target type via existing `TypeRef`/refs and lets the adapter's typed `AddPersistentListener`
  API repopulate them — avoids assembly-qualified-name drift across Unity versions.
- Object-mode argument is a full second reference (target **and** arg can each be scene-or-asset);
  both must resolve or the listener is a conflict.
- `UnityEventTools` lives in `UnityEditor.Events` — Editor-only, correctly on the adapter side of the
  §2 seam; Core never sees it.
- Overload ambiguity when a method name exists as both zero-arg and one-arg: authoring arg presence
  disambiguates; Reconcile trusts the snapshot's `m_Mode`, not name lookup.
