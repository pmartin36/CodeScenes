# M8 — UnityEvents / OnClick wiring

> ## AMENDED 2026-07-28 — supersedes conflicting detail below (read first)
> The compiler-safety toolkit and typed selectors shipped after this spec was written. M8 must be built
> to match what already exists:
> - **The method reference is a typed method-lambda, not `nameof`.** The wiring shape is
>   `.OnClick(<target>, x => x.Method(args))`, where `x` is typed as the target component and a static
>   argument is written inside the call (`l => l.SetLevel(3)`). Every `nameof(...)` example in this
>   document (authoring API, test plan, checklist) is superseded by the lambda form.
> - **The SB1201/SB1202 UnityEvent-signature analyzer already ships** (delivered under spec 26) in
>   `CodeScenes.Analyzers/UnityEventAnalysis.cs`: SB1201 arity mismatch (Error), SB1202 non-public target
>   (Warning). It is deliberately inert and recognizes ONLY the lambda form (`TryFindMethodLambdaArg`); it
>   never matches `nameof`. So M8's job is to build the authoring + Core round-trip surface that makes
>   these diagnostics bind and fire. The out-of-scope line "signature validation deferred to a later
>   analyzer" is now backwards and void: the analyzer waits on M8, not the reverse.
> - **Target model RESOLVED 2026-07-30 (was "still open").** Grounded in shipped code it is one new
>   authoring type plus a resolution change, NOT a model change:
>   - `ComponentHandle<T>` already exists (`com.codescenes/Runtime/ComponentHandle.cs:14`) as the
>     configuration handle passed INTO `Component<T>(c => ...)`.
>   - `ObjectRef(string? TargetLogicalId)` (`SceneBuilder.Core/Model/ValueNode.cs:130`) is already just a
>     LogicalId string with nothing restricting it to GameObjects, and the IdentityMap already carries
>     `Kind=="Component"` entries.
>
>   Add `ComponentRef<T>`, captured off a node via `NodeHandle.Ref<T>(int ordinal = 0)`:
>   ```csharp
>   var opener = scene.Add("Door").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();
>   scene.Add("QuitButton").Component<Button>(b => b.OnClick(opener, o => o.Open()));
>   ```
>   `Ref<T>()` needs its own name because `Component<T>()` is taken (`NodeHandle.cs:87`) and returns
>   `NodeHandle`; C# cannot overload on return type. The ordinal maps onto the existing component
>   LogicalId scheme (Type.FullName + ordinal-within-type), so two components of one type on one
>   GameObject stay addressable — the reason the explicit-handle decision was banked over inferring the
>   component from the lambda's receiver type. `ComponentRef<T>` is reference-only, deliberately distinct
>   from `ComponentHandle<T>`, so the compiler rejects configuring a component from outside its lambda.
>
>   Still open for the build to settle: the dynamic form. `h => h.SetValue` is a method GROUP, not an
>   `Action<T>`, so check what the shipped `TryFindMethodLambdaArg` does with a method group before
>   fixing the parameter type.
> - **Still unbuilt:** the Core round-trip (`ValueNode.UnityEventListeners`, `SetUnityEvent`, adapter
>   read/write) as specified below.
>
> ### DECOMPOSITION CONSTRAINT (learned from M-UI, 2026-07-30 — do not ignore)
> **Component-target resolution is this milestone's cross-cutting invariant.** "A listener target is a
> component `LogicalId`, not a GameObject `LogicalId`" must hold in authoring, parse, the model, the
> differ, materialize, adapter read, adapter write, and reconcile. Give it **ONE owning task with ONE
> shared implementation that every other task calls.** Do NOT restate it as per-task guidance.
> M-UI stated its equivalent invariant (X/Y belongs to `anchoredPosition`) as guidance across six tasks;
> the seam pass then kept finding the task that forgot, re-opened committed buckets, and the milestone
> ran ~22 hours and ~13M tokens. That is the specific failure this constraint exists to prevent.
> - **Reuse, do not reinvent:** the `.OnEvent(x => x.onValueChanged, …)` event-field selector is the same
>   member-chain-from-source parser shipped for façade selectors and `.Set(x => x.field)`. Asset targets
>   and object-mode asset arguments ride the shipped typed asset catalog (spec 28, emit-typed-by-default
>   `Assets.<Type>…`), not string `Asset("path")`.

> ## Target-model decision (banked 2026-07-21 — authoritative)
> A persistent listener's target is the **specific component**, not the owning GameObject (Unity stores
> `m_Target` as the exact `Object`; two components on one GameObject can share a method name). So:
> - **The listener target is an EXPLICIT component handle**, not a GameObject handle with the component
>   inferred from the method's declaring type. M8 introduces a way to capture a component as a handle
>   (e.g. `var opener = door.Component<DoorOpener>(…)` yielding a component handle) and pass **that** as the
>   `.OnClick(...)` / `.OnEvent(...)` target. The examples in the body that pass a GameObject handle
>   (`.OnClick(door, …)`) are superseded by the component-handle form.
> - **`ObjectRef` must be able to address a component's `LogicalId`.** Core's authored `ObjectRef` today
>   only ever targets GameObjects (handles are GameObject-only); M8 extends the handle model + `ObjectRef`
>   resolution so a component `LogicalId` (the IdentityMap already has `Kind=="Component"` entries) is a
>   valid target. A method on the GameObject itself (e.g. `GameObject.SetActive`) still targets the GO.
> - **Emitted method refs use the typed method-lambda** `x => x.Method(args)` (see the AMENDED banner
>   above; the earlier `nameof(...)` decision is superseded). The lambda receiver is the target component
>   type; no general `using`-injection is added. An unchanged listener's authored token is preserved
>   verbatim (span-local reconcile); only new appends use the lambda form.

## ALREADY SHIPPED — do not re-plan this (commit `30a9613`, 2026-08-06)

The model layer of this milestone is BUILT, GATE-GREEN and COMMITTED. A decomposition of this spec must
plan only what remains, and must treat the following as existing code to build ON, not work to schedule.
Gate at the time: `GATE PASS: Core + Unity EditMode green (passed=657 failed=0 skipped=0)`.

- `ValueNode.UnityEventListeners` + the `UnityEventListener` record, registered in the polymorphic JSON
  union, with `ValueWalk` descending into each listener's `Target` and `ArgValue`
  (`SceneBuilder.Core/Model/UnityEventListener.cs`, `ValueNode.cs`, `ValueWalk.cs`).
- The component-target CLASSIFIER: one total function to a discriminated result over
  SceneObject / Asset / DanglingNull / Unresolvable, applied to a listener `Target` and an object-mode
  `ArgValue` alike, plus the scan that fails a site classifying a reference without it
  (`SceneBuilder.Core/Identity/ComponentTargetResolution.cs`, `SceneBuilder.Core.Tests/ListenerTargetBypassScanTests.cs`).
- The model <-> persistent-call PROJECTION, total and reversible over all seven modes
  (`SceneBuilder.Core/Model/UnityEventProjection.cs`), with the mode and call-state numbers MEASURED
  against a live editor. That measurement is what corrected this spec's own `UnityEventCallState` line.
- The EditMode fixture surface (`unity-gate/Assets/Fixtures/UnityEventFixtures.cs`, and
  `DoorOpenerFixture.cs` EXTENDED with `Open()` — it has one; do not plan to add it).

**Foundation gaps measured DURING that run, which the remaining work must own.** These are recorded with
their file:line evidence in `docs/m8-measured-defects.md`; read it before planning, because each cost a
live editor run to establish and none is derivable from this spec:
- A component's own `GlobalObjectId` reaches Core NOWHERE on the snapshot, so §13 convergence for a
  component listener target has no data path.
- The public C# member spelling (`m_OnValueChanged` -> `onValueChanged`) needed to render
  `.OnEvent(x => x.onValueChanged, ...)` is adapter-side only and likewise absent from the snapshot.
- `ValueNode.Primitive.Value` normalises a boxed `JsonElement` for equality/hashing ONLY, so a
  `SetUnityEvent` op deserialized from plan JSON and then applied throws `InvalidCastException`.
- The component-LogicalId bypass scan allowlists per FILE, and three of those files are where the
  remaining listener code lands, so the format invariant is unguarded exactly there.

Both of the first two are the same shape: an ADAPTER task needing a `SceneBuilder.Core/Model/*` addition.
Hoist all snapshot-model additions into ONE owning task that declares those files; splitting them across
the adapter tasks that consume them is what forced two separate re-plans.

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
- Building the SB1201/SB1202 signature analyzer: it ALREADY ships (see AMENDED banner). M8's obligation
  is to build the typed method-lambda authoring surface so those diagnostics bind and fire in the IDE and
  the gate; M8 still fails loud at Materialize when Unity rejects a wire.

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
The method is a **typed method-lambda** on the target component; the static argument (if any) is written
inside the call, so the compiler and the shipped SB1201/SB1202 analyzer check the signature.
```csharp
// Button convenience (m_OnClick) — target is a component handle, method is a typed lambda on it:
scene.Add("Button").Component<Button>(b => b.OnClick(opener, o => o.Open()));              // void
scene.Add("Button").Component<Button>(b => b.OnClick(audio, a => a.Play()));               // void, asset-less scene target
scene.Add("Button").Component<Button>(b => b.OnClick(lamp, l => l.SetLevel(3)));           // int arg (captured from the call)
scene.Add("Button").Component<Button>(b => b.OnClick(mixer, m => m.SetVol(0.5f),           // float arg + call state
                                                     callState: UnityEventCallState.EditorAndRuntime));

// Generic UnityEvent field on any component (event-field selector reuses the shipped .Set(x => x.field) parser):
b.OnEvent(c => c.onValueChanged, hud, h => h.Refresh());                                    // bool/int/etc. per field type

// Dynamic — forward the event's own arg(s) to a matching method (covers multi-arg UnityEvent<T…>):
slider.Component<Slider>(s => s.OnEvent(x => x.onValueChanged, hud,
                                        h => h.SetValue, dynamic: true));                   // ArgMode "dynamic", no stored value
```
- `target` is a **component handle** (→ `ObjectRef` addressing a component `LogicalId`, per the target-model
  banner) or an asset reference (→ `AssetRef`, authored via the typed asset catalog `Assets.<Type>…`, spec 28).
- The method-lambda's receiver is typed as the target component, so a wrong or non-existent method fails to
  compile (and SB1201/SB1202 flag arity / non-public before Materialize).
- Lowering (Editor→Core): `.OnClick(...)`/`.OnEvent(...)` → `ComponentData.Fields[eventPath]` =
  `UnityEventListeners([...])`; the lambda's method name and in-call argument select `ArgMode`/`ArgValue`;
  default `callState` = `RuntimeOnly`.

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
- **Round-trip void**: SceneModel with `.OnClick(opener, o => o.Open())` → serialize → parse → equal model.
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
1. Open a scene with a `Button` and a `DoorOpener` component (`Open()` public). Author
   `.Component<Button>(b => b.OnClick(opener, o => o.Open()))`, Materialize → in Unity the Button's
   OnClick shows one persistent entry: target `opener`, function `DoorOpener.Open ()`, RuntimeOnly. **Expected:** wired, no errors.
2. In Unity, change the OnClick target to a different component → save. **Expected:** source's
   `.OnClick(...)` target argument updates to the new component handle; map unchanged for other entries.
3. In Unity, change the invoked method to a 1-arg `int` method and set the value to `7` → save.
   **Expected:** source becomes `.OnClick(target, t => t.Method(7))` (ArgMode int, value 7).
4. In Unity, set the OnClick target to a **project asset** (e.g. an `AudioSource` on a prefab asset or
   an asset object arg) → save. **Expected:** source arg becomes the asset reference; `Assets[]` gains
   its GUID.
5. In Unity, set the entry's call state to **Editor And Runtime** → save. **Expected:** source gains
   `callState: EditorAndRuntime`.
6. In Unity, remove the OnClick entry → save. **Expected:** the `.OnClick(...)` statement is removed
   from source.
7. In Unity, wire a `Slider.onValueChanged` **dynamically** to a `Hud.SetValue(float)` method (drag it
   under the "Dynamic float" section) → save. **Expected:** source becomes
   `.OnEvent(x => x.onValueChanged, hud, h => h.SetValue, dynamic: true)`; round-trips.
8. Re-run Materialize with no code change. **Expected:** no plan ops (idempotent).

## Dependencies
- **M3** (components + serialized fields; `ComponentData.Fields` by `propertyPath`).
- **M5** (`ObjectRef` handle resolution; scene-object targets).
- **M4** (`AssetRef` resolve/persist; asset targets and object-mode asset args).
- **M2** (Reconcile + Roslyn `SourcePatch` machinery for statement/arg edits).

## Risks/notes
- **Mode ↔ ArgMode table** (`PersistentListenerMode`): `0 EventDefined`→`dynamic`,
  `1 Void`→`void`, `2 Object`→`object`, `3 Int`→`int`, `4 Float`→`float`, `5 String`→`string`,
  `6 Bool`→`bool`. `UnityEventCallState`: `0 Off`, `1 EditorAndRuntime`, `2 RuntimeOnly` — CORRECTED 2026-08-06 from
  MEASUREMENT against the real editor during M8's b1-t3 (this document previously had RuntimeOnly and
  EditorAndRuntime swapped; the authority is `SceneBuilder.Core/Model/UnityEventProjection.cs`, which
  pins the numbers with an EditMode assertion). In dynamic
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
