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

**ALSO SHIPPED (second b1, commit `3325907`):** `Reconciler.cs` was extracted (966 -> 852 lines) and
`ComponentReconciler` made partial, so the later reconcile tasks are not fighting over 34 lines of
headroom; and the four foundation gaps below were CLOSED — the component `GlobalObjectId` and the public
C# member spelling now reach Core on the snapshot, `ValueNode.Primitive` normalises a boxed `JsonElement`
at its use sites, and the component-LogicalId bypass scan landed. Do not re-plan any of it.

**ALSO SHIPPED (b1-t3, commit `e1ae5d5`; committed WIP but GATE-GREEN at `passed=663`).** The entire
authored surface and its parse/recognizer path are built and Core-test-covered. A decomposition must
build ON these, not schedule them:
- `ComponentRef<T>` (`com.codescenes/Runtime/ComponentRef.cs`) — reference-only, with `T As()` for the
  object-argument cast — and `NodeHandle.Ref<T>(int ordinal = 0)` (`com.codescenes/Runtime/NodeHandle.cs`)
  returning it, distinct from `ComponentHandle<T>`.
- The full `.OnClick` / `.OnEvent` overload set (`com.codescenes/Runtime/ComponentHandle.cs`): the
  `ComponentRef` / `AssetReference` / `SceneObjectHandle` (GameObject) targets, each with optional
  `callState`; the `.OnEvent(x => x.eventField, …)` event-field selector (reusing the shipped
  `.Set(x => x.field)` member-chain parser); and the `dynamic:` method-group forms for
  `UnityEvent<TArg>` and `UnityEvent<TArg0,TArg1>`.
- The Editor→Core lowering and the parse/recognizer path for every form
  (`SceneBuilder.Core/Parsing/BuilderParser.UnityEvents.cs`, `FlatShapeRecognizer.UnityEvents.cs`),
  green under `RecognizerAgreementTests`, `UnityEventAuthoringParseTests`, `UnityEventAuthoringFormsTests`,
  `UnityEventAuthoringBindTests`, `UnityEventAuthoringDocGenTests`.
- SB1201 (arity) / SB1202 (non-public target) for `.OnClick` only
  (`CodeScenes.Analyzers/UnityEventAnalysis.cs`), green under `ScaffoldAndSeverityTests`.

**ALSO SHIPPED (b1 completion, commit `f040769`):** the Plan op `SetUnityEvent` plus its Diff and
Materialize path are BUILT, headless and pure-Core (`SceneBuilder.Core/Plan`, `Diff/Differ.cs`,
`Materialize/Materializer.cs`). The `SetUnityEvent` op is defined here and consumed downstream by the
adapter's `PlanExecutor` and by Materialize/Diff. A decomposition builds ON this op, it does not re-plan
it.

**The `callState:` member-name rejection is SHIPPED in f040769.** An unknown `callState` name is now
rejected AT the recognizer (`FlatShapeRecognizer.UnityEvents.cs`), so recognizer and parser agree the
shape is not a listener, and `ReadCallState` (`BuilderParser.UnityEvents.cs`) raises a located
`ParseException` instead of the `InvalidOperationException` that `RecognizerAgreementTests` would miss (it
catches only `ParseException`). A `RecognizerAgreementTests` corpus case pins a bogus `callState` name.

**SB1201 arity for `.OnEvent` is SHIPPED in f040769** (`CodeScenes.Analyzers/UnityEventAnalysis.cs`). Both
forms now flag:
- Static / void form (`h => h.Method(args)`): the SAME within-lambda arity check `.OnClick` runs
  (referenced method's parameter count vs args supplied in the lambda body; more than one static arg is
  illegal), no event-type derivation needed.
- Dynamic form (`h => h.SetValue`, a method GROUP under `dynamic: true`): the `x => x.eventField` selector
  resolves to its symbol via the semantic model, that field's `UnityEvent<T0..Tn>` generic arguments are
  read, and SB1201 fires when NO candidate of the method group's symbol matches `(T0..Tn)`. Only a
  genuinely-underivable site (selector does not resolve, or a non-generic `UnityEvent` paired with a
  dynamic method group taking args) stays unflagged and relies on Materialize fail-loud.
`ScaffoldAndSeverityTests` pins cases for both forms.

**THE ADAPTER'S SERIALIZED-PATH VOCABULARY — decide this ONCE, in one task, before either adapter file
exists.** Learned by getting it wrong twice. The invariant "the adapter carries no mode/arg logic"
(09:219-220) is only enforceable if the adapter never spells a serialized path itself, and the guard that
polices it, `ModeArgBypassScanTests`, has two interacting traps:
- Its Scan A allowlist was seeded with `UnityEventWriter.cs` and `UnityEventReader.cs` — files that did
  not exist yet — so the adapter is unguarded from the moment it appears.
- Its token list matches STRING LITERALS, and includes `m_Mode`, `m_Arguments`, `m_IntArgument` and the
  rest. So simply removing the allowlist entry turns the adapter's own MANDATORY code
  (`FindPropertyRelative("m_Mode")` on the `m_PersistentCalls.m_Calls` + `SerializedObject` route) into a
  violation and a red gate.
The only shape that works: ONE task owns a Core-side vocabulary of the serialized spellings, placed in
`UnityEventProjection.cs` (already the sole allowlisted Core file), covering the COMPLETE field set from
09:179-181 — `m_Target`, `m_MethodName`, `m_Mode`, `m_CallState`, `m_Arguments` and its
`m_ObjectArgument`/`m_IntArgument`/`m_FloatArgument`/`m_StringArgument`/`m_BoolArgument`, plus
`m_TargetAssemblyTypeName` and `m_ObjectArgumentAssemblyTypeName`, and the `m_PersistentCalls.m_Calls`
root. A SUBSET does not work: any spelling left out is one both adapter files then invent independently,
which is the divergence the hoist exists to prevent. That task also removes both allowlist entries and
adds the missing spellings to the scan's token list, so the guard covers what it claims to.
`PersistentCallFields` carries C# property names only and is NOT that vocabulary.

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

## REMAINING WORK — measured architectural constraints the build MUST own

Read `docs/m8-measured-defects.md` before decomposing: every entry there was measured against real code
or a real editor, and its measurements are true even where a named task-id is void or an entry is marked
RESOLVED (skip the RESOLVED ones — e.g. the `UnityEventCallState` numbers, the gate engine-message flake,
and the `ValueNode.Primitive` JSON-boundary cast are all CLOSED). The architecture-shaping constraints
below are the ones that change the decomposition; each is stated as owner + mechanism, not prose, so a
task owns it and a check fails when a site bypasses it:

- **Reconcile must treat a component `LogicalId` as a resolvable target (the cross-cutting invariant's
  Reconcile-side mechanism).** The shipped reconciler builds its resolvable-target set from GameObject /
  PrefabInstance nodes and authored handle names only; a listener `Target` carrying a component
  `LogicalId` is therefore classified DANGLING, so EVERY listener the milestone produces would report a
  conflict and emit no patch. The owning component-target-resolution task must add component identities to
  the reconciler's resolvable-target set AND to the handle table used for the render — not merely dispatch
  on the new value kind. (`SceneBuilder.Core/Reconcile/Reconciler.cs`,
  `ComponentReconciler.ClassifySnapshotRef`.)
- **The scene→code source render needs a real `.OnClick` / `.OnEvent` arm for
  `ValueNode.UnityEventListeners`.** Today the render path (`SceneBuilder.Core/Reconcile/SourceExpr.cs`
  `ValueNodeLiteral`, reached via `ComponentReconciler.RenderFieldValue` and the appended-component/object
  sites in `ComponentPatchApplier.cs` / `SourceEdit.cs`) throws loudly on this kind, naming the required
  route. The reconcile task must declare those render files in its TOUCHES and route the kind to the
  authoring call, rather than leaving the throw.
- **The reconcile listener->source-patch work MUST decompose into at least TWO tasks, never one.** The
  reverted attempt was ONE oversized task: it bundled the cross-cutting invariant, the render arm, and
  every delta type, then pruned a symmetric operation set, so the ADD path shipped untested and corrupted
  source. Split it:
  - **Task 1, INVARIANT OWNER (the cross-cutting invariant's Reconcile-side mechanism).** Add component
    identities to the reconciler's `resolvableTargets` set AND to the handle table, so a listener `Target`
    carrying a component `LogicalId` is Resolvable, not Dangling. Route target-kind resolution (`ObjectRef`
    vs `AssetRef` from the snapshot target, else a surfaced `Conflict.UnsyncableListener`) through the
    shipped `ComponentTargetResolution` classifier. ONE owning task, ONE shared mechanism every reconcile
    consumer calls.
  - **Task 2 (and a third if needed), PER-DELTA SOURCE RENDER.** The `.OnClick` / `.OnEvent` render arm
    plus the per-operation source edits. Every file each task edits belongs in its TOUCHES: `SourceExpr.cs`,
    `ComponentReconciler.cs`, `ComponentPatchApplier.cs`, `SourceEdit.cs`, `Reconciler.cs`, the NEW
    source-patch partial-class module, the parser span-projection files `BuilderParser.Projections.cs` /
    `BuilderParser.UnityEvents.cs`, and `SceneBuilder.Core.Tests/AuthoringBindHarness.cs`. Do NOT leave an
    implicated file out of TOUCHES.
  - **File-size budget:** land the listener source-patch logic in a NEW partial-class file so
    `SourcePatchApplier.cs` stays under 1000 lines (`ObjectRefDescentScanTests` budget).
- **§13 convergence for a NEW component target.** The reconciler's pending-target set is keyed on
  GameObject `GlobalObjectId`s, so a component's own goid is never Pending and an unmapped new component
  target falls to DANGLING (permanent conflict) instead of deferring and converging on the guaranteed
  second Sync. The §13 task must make a new component target reach the pending set. (Note the boundary:
  a component reached ONLY through a prefab-instance channel — an instance's added components / added
  GameObjects — has no goid stamped on the snapshot today; the §13 task decides whether that is in scope
  or an explicitly-stated boundary.)
- **The incremental snapshot read-cache must invalidate on component-entry change.** The adapter's
  per-GameObject node cache (`com.codescenes/Editor/ChangeScopedSnapshot.cs`, keyed via
  `SceneRefResolver.Generation`) is keyed on a generation over MAPPED NODES only; a sync that adds or
  retargets `Kind=="Component"` entries leaves the key unchanged, so the cache serves a stale listener
  target. The read task must key the cache on a generation that also moves for component changes (b1-t2
  shipped `ComponentTargetIndex.Generation`) and declare `SceneRefResolver.cs` + `ChangeScopedSnapshot.cs`
  in its TOUCHES.
- **Adapter WRITE mechanism is `SerializedObject` over `m_PersistentCalls.m_Calls`, not the typed
  `Add*PersistentListener` family (MEASURED).** The typed `UnityEventTools.Add*PersistentListener` family
  takes typed delegates the adapter would have to synthesize for an arbitrary reflected method, the object
  overload is generic, and there is NO Add* overload for the dynamic (EventDefined, mode 0) case at all.
  The realistic mechanism — and the one `PersistentCallFields` is shaped for — is editing
  `m_PersistentCalls.m_Calls` through `SerializedObject` + `ApplyModifiedPropertiesWithoutUndo`
  (optionally seeded by the parameterless `AddPersistentListener`), then `SetPersistentListenerState`.
- **An unsyncable listener surfaces as a WARNING, not a NOTE.** A listener the user wired in the Inspector
  that is not reaching their code is the same fail-loud (§7/§13) class as `UnrepresentableValue`; its
  conflict report (`com.codescenes/Editor/ConflictSurfacing.cs`) must read WARNING.
- **File-size budget.** `SceneBuilder.Core/Reconcile/SourcePatchApplier.cs` sits near the 1000-line budget
  enforced by `SceneBuilder.Core.Tests/ObjectRefDescentScanTests.cs`. The source-patch listener task must
  fit within it by SPLITTING the file (a pure partial-class extraction) rather than growing it; do not
  regress the budget silently.

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
- Building the SB1201/SB1202 signature analyzer from scratch: it ALREADY ships for `.OnClick` (see
  AMENDED banner). M8's obligation is to build the typed method-lambda authoring surface so those
  diagnostics bind and fire in the IDE and the gate; M8 still fails loud at Materialize when Unity rejects
  a wire. **SB1201 arity for `.OnEvent`** (both the static/void and the dynamic method-group forms) is now
  SHIPPED in f040769, extending the analyzer past the v0 under-flag.

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
  **Sequencing constraint (a real cross-direction dependency — decompose accordingly).** There are TWO
  levels of no-op idempotence proof and they do not belong to the same task. The pure-Core proof
  (`Diff`/`Materialize` over a hand-built desired list and an equal hand-built `SceneSnapshot` ⇒ zero
  ops) is headless and belongs with the `Diff`/`SetUnityEvent` work. The **live-scene** proof (a second
  `Materialize` against the REAL editor scene after a write emits ZERO plan ops) cannot hold until the
  scene→code **READ** exists: `Materialize` diffs desired against a snapshot read from the live
  component, and until the adapter reads a `UnityEvent` field back AS `ValueNode.UnityEventListeners`
  (today `m_OnClick` surfaces as an unrepresentable note and `Diff` has no `SetUnityEvent` handling),
  `actual` reads Unsupported while `desired` is `UnityEventListeners`, so a `SetUnityEvent` op re-emits
  on every pass. Therefore the live "second Materialize = zero ops" EditMode assertion must DEPEND ON the
  listener-read task (or ride the bidirectional round-trip task that already depends on both directions);
  it must NOT sit in a write-only bucket that declares no dependency on the reader.
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
- **Diff idempotence** (pure-Core, headless): identical hand-built desired/actual listener lists → zero
  `SetUnityEvent` ops. This is the Core-level proof and needs no adapter reader. The LIVE-scene
  second-Materialize-emits-zero-ops proof is separate and carries the reader dependency stated under
  "Materialize → Plan".
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

### Reconcile source-patch: required RED matrix (symmetric operation set, pin the WHOLE set not a subset)
The reverted attempt pruned this set and shipped the ADD path untested; it corrupted source. Per the
"symmetric operation set: pin the whole set" rule, the decomposition and the test-writer MUST pin every
case below, not a chosen subset:
1. **ADD preserves existing and order:** source `.OnClick(first, o => o.Open())`, snapshot `[first,
   second]` -> a NEW `.OnClick(second, ...)` statement is inserted, `first` untouched, final order
   `[first, second]`. Never overwrite an existing listener's span.
2. **TARGET CHANGE in place:** a listener whose target changes -> the `.OnClick(...)` target ARGUMENT is
   edited in place; the statement is NOT deleted-and-reappended, and ordering is preserved. A same-position
   target change MUST be detected as a CHANGE, not remove(old)+add(new) (which would reorder). Key the
   listener match so this holds.
3. **METHOD / ARG / CALLSTATE change:** each is an in-place argument edit of the existing statement.
4. **REMOVE:** a dropped listener deletes the matching `.OnClick(...)` statement; an empty list clears the
   field.
5. **MULTIPLE adds in one field:** all inserted, ordered, non-overlapping edits.
6. **ADD+REMOVE and ADD+CHANGE on the same field:** all edits apply, non-overlapping.
7. **Non-last-index / reorder:** a listener added at a non-final index, or reordered, preserves the
   scene's array order in source.
8. **`.OnEvent(x => x.field, ...)` form:** the full matrix above holds for the generic OnEvent form, not
   just `.OnClick`.
9. **Dynamic method-group form** (`h => h.SetValue`, `dynamic: true`): round-trips through reconcile.
10. **Fail-loud / conflict:** a target resolving to no `LogicalId` and no asset -> surfaced conflict, no
    patch; an empty `MethodName` -> located fail-loud report (object+field+location).

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
