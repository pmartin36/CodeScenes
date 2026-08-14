# Spec 41: Snapshot-emit classification and the added-child field channel

Three defects reproduced by an out-of-tree probe over the public `Reconciler.Reconcile` /
`SourcePatchApplier.Apply` against the current tree. They share one root cause: a snapshot value that
reaches source emission WITHOUT going through the shared classification
(`SnapshotFieldEmission.EmitFieldSet` / `ComponentReconciler.ClassifySnapshotRef`) is emitted wrong.
Two emit non-compiling source silently, the third crashes the whole Apply pass. CLAUDE.md rates a
write path that can emit non-compiling source, and a crash, as bugs outright, not style.

## Measured defects

### Defect 1: a bare `ValueNode.Unsupported` renders its raw token, at three sites, silently

`SourceExpr.ValueNodeLiteral` renders `ValueNode.Unsupported unsupported => unsupported.RawToken`
(`SceneBuilder.Core/Reconcile/SourceExpr.cs:120`). A bare (non-list) `Unsupported` whose `RawToken`
is not a compiling C# expression therefore emits that token verbatim.

Measured, three sites, each producing `targets = ObjectReference` (the raw token) in `Fields` with
`conflicts=0 edits=0` and no report, then `CS0103` at compile:
- `ComponentReconciler.EmitComponentAppend`, via `SnapshotFieldEmission.EmitFieldSet`.
- `Reconciler.BuildAddInstanceComponent`, via `SnapshotFieldEmission.EmitFieldSet`.
- `ReconcilerInstances.BuildOverrideSetSpec`, via `ComponentReconciler.RenderFieldValue`.

The classification does not catch it. `ClassifySnapshotRef` checks `ListValueEmission.HasUnemittableItem`
first, and that predicate only inspects a `List` whose items are `Unsupported`
(`SceneBuilder.Core/Reconcile/ListValueEmission.cs:75-77`); a bare top-level `Unsupported` is not a
list, so it falls through to `RefResolution.NotObjectRef` and stays in `Fields`. The two `EmitFieldSet`
sites classify every field and still admit this value; `BuildOverrideSetSpec` never classifies at all.

### Defect 2: `BuildOverrideSetSpec` renders a dangling/pending `ObjectRef` as a phantom identifier

`ReconcilerInstances.BuildOverrideSetSpec` (`SceneBuilder.Core/Reconcile/ReconcilerInstances.cs:510-530`)
calls `ComponentReconciler.RenderFieldValue` at line 528 with NO classification. Measured
`RenderFieldValue(ObjectRef("ghost-1"))` where `ghost-1` is unmapped produced
`ValueExpression=ghost-1 edits=0` with no conflict, then `CS0103` at compile. This is the one
snapshot-emit site still bypassing the `Unemittable`/`Dangling`/`Pending` guard the sibling
`BuildAddInstanceComponent` gained through `EmitFieldSet`.

### Defect 3: an added-child component `ObjectRef` field crashes the sync pass

`ReconcilerInstances.Nested.cs` `ReconcileAddedGameObjects` (:85) builds an `AppendInstanceAddChild`
edit carrying the added child's components in its `Node` (:137-145).
`SourcePatchApplier.Instances.cs` `RenderAddChildClosure` (:234-254) renders those components with
`RenderComponentClosureArgs(c.Fields, null)` at line 244, passing null field-expressions.
`AppendInstanceAddChild` (`SceneBuilder.Core/Reconcile/SourceEdit.cs:368-381`) has no
`FieldExpressions` channel, so an `ObjectRef` field has no pre-rendered handle and falls to
`SourceExpr.ValueNodeLiteral`, which has no `ObjectRef` arm and hits its
`_ => throw new NotSupportedException(...)` (`SceneBuilder.Core/Reconcile/SourceExpr.cs:124`). Measured:
`NotSupportedException` on `SourcePatchApplier.Apply`, killing the whole Apply.

## Goal

Every snapshot value that reaches source emission is emitted through the shared classification. A
value the classification cannot represent (a bare `Unsupported`, a dangling or pending `ObjectRef`) is
omitted from the emitted source and surfaced through the located conflict channel, never written as a
raw token or a phantom identifier. An added-child component's reference and catalogued-asset fields
render a real handle instead of crashing Apply.

## In scope

- Route the two `EmitFieldSet` sites and `BuildOverrideSetSpec`/`RenderFieldValue` through one
  classification so a bare `Unsupported` value is omitted and reported at all three (Defect 1).
- Classify inside `BuildOverrideSetSpec` so a dangling or pending `ObjectRef` is omitted and reported
  rather than rendered as an identifier (Defect 2).
- Add a `FieldExpressions` channel to `AppendInstanceAddChild`, threaded from `ReconcileAddedGameObjects`
  through the same `SnapshotFieldEmission` classification, so an added-child component's
  `ObjectRef`/`AssetRef`/list field renders a pre-rendered handle instead of crashing (Defect 3).
- A structural check that fails when a snapshot-emit site renders a value without the classification.

## Out of scope

- The root-append `CS0841` self-reference variant (a fourth suspected defect) is a SEPARATE,
  live-probe-pending item, NOT this spec. It is not reproduced here and is not fixed here.
- Performance of the reconcile pass. The classification already runs on the two `EmitFieldSet` sites;
  extending it to the third site and to the added-child path adds no new walk order of concern.
- Unrelated `PlanOp` / conflict registry entries. This spec adds no new Core value type; its only new
  edit shape is the `FieldExpressions` channel on `AppendInstanceAddChild`.

## Additions to the contract

No new Core value type. `RefResolution` (`ComponentReconciler.cs:561`), `SnapshotFieldEmission`,
`ConflictKind.UnrepresentableValue` and `ConflictKind.DanglingReference` already exist and are reused.

The one new edit shape is a `FieldExpressions` channel on `AppendInstanceAddChild`
(`SceneBuilder.Core/Reconcile/SourceEdit.cs:368`), mirroring the pre-rendered-handle channel that
`AppendInstanceAddComponent`, `AppendComponentStatement` and `ScopedAddComponent` already carry
(`IReadOnlyDictionary<string, string>? FieldExpressions`, `SourceEdit.cs:172/294/328`). It is threaded
into `RenderAddChildClosure` so `RenderComponentClosureArgs` receives a real expression map instead of
null.

## The one cross-cutting mechanism: every snapshot-emit site routes through the classification

Defects 1, 2 and 3 are one root cause: a snapshot value reaching emission outside
`SnapshotFieldEmission.EmitFieldSet` / `ClassifySnapshotRef`. The fix is one decision applied at every
site, not three local patches.

**Classification is the single gate.** No snapshot-emit site renders a value straight through
`SourceExpr.ValueNodeLiteral` or `ComponentReconciler.RenderFieldValue` without first passing it
through `SnapshotFieldEmission.EmitFieldSet` (which classifies via `ClassifySnapshotRef`). The two
`EmitFieldSet` callers already do; `BuildOverrideSetSpec` and the `AppendInstanceAddChild` payload path
are brought onto the same gate.

**The bare-`Unsupported` hole is closed inside the classification, once.** `ClassifySnapshotRef` today
returns `NotObjectRef` for a bare `Unsupported` because `HasUnemittableItem` only inspects list items.
A bare `Unsupported` whose `RawToken` is not a compiling expression is `Unemittable`, decided in the
shared classifier so every current and future caller inherits it. A caller that renders a value the
classifier calls `NotObjectRef` may still assume it is emittable, so the unemittable verdict must be
produced BY the classifier, not left to each site to re-derive.

**The bypass check.** A Core test asserts that for every snapshot-emit site, a bare non-compiling
`Unsupported` field and an unmapped `ObjectRef` field are omitted from the emitted `Fields`/
`ValueExpression` and reported, so no site can render either as a raw token or an identifier. The check
drives all three sites (`EmitComponentAppend`, `BuildAddInstanceComponent`, `BuildOverrideSetSpec`) and
the added-child path, not one site trusted for the rest.

## Report-channel contract for unemittable and dangling values

One channel, the one `SnapshotFieldEmission.EmitFieldSet` already uses, matching foundation section 7
("fail loud, located; no silent drops") and section 13 point 2 ("report every deferred piece"):

- A value the classifier rules `Unemittable` (a bare non-compiling `Unsupported`, or a list carrying
  an unemittable item) is OMITTED from the emitted source and surfaced as a
  `ConflictKind.UnrepresentableValue` conflict, built through `ConflictDetector.UnrepresentableListItem`'s
  sibling factory family (`ConflictDetector.cs:355`), carrying the component `LogicalId`, the component
  type full name and the field key.
- A value the classifier rules `Dangling` (an `ObjectRef` whose target is neither resolvable nor
  pending) is OMITTED and surfaced as a `ConflictKind.DanglingReference` conflict
  (`ConflictDetector.DanglingReference`, `ConflictDetector.cs:213`), naming the dangling target.
- A value the classifier rules `Pending` (a same-batch create candidate with no IdentityMap entry yet)
  is OMITTED silently and converges on the guaranteed second sync (section 13 point 2).

Never a raw token, never a phantom identifier. The three currently-broken sites (the two `EmitFieldSet`
paths and `BuildOverrideSetSpec`/`RenderFieldValue`) adopt this at once, so no site is left surfacing
an unrepresentable value through emission instead of the report channel.

## Core deliverables

- `ComponentReconciler.ClassifySnapshotRef` (`SceneBuilder.Core/Reconcile/ComponentReconciler.cs:570`)
  returns `Unemittable` for a bare `Unsupported` value whose `RawToken` is not a compiling C#
  expression, so the two `EmitFieldSet` sites already omit-and-report it.
- `ReconcilerInstances.BuildOverrideSetSpec` (`SceneBuilder.Core/Reconcile/ReconcilerInstances.cs:510`)
  classifies its rendered value before calling `RenderFieldValue`: an `Unemittable`/`Dangling` value
  omits the override set and reports it through the channel above; a `Pending` value omits it silently;
  a `Resolvable`/`NotObjectRef` value renders as today.
- `AppendInstanceAddChild` (`SceneBuilder.Core/Reconcile/SourceEdit.cs:368`) gains
  `FieldExpressions : IReadOnlyDictionary<string, string>?`, and `ReconcileAddedGameObjects`
  (`SceneBuilder.Core/Reconcile/ReconcilerInstances.Nested.cs:85`) routes each added child's components
  through `SnapshotFieldEmission.EmitFieldSet`, populating that channel with pre-rendered handles for
  `ObjectRef`/catalogued-`AssetRef`/list fields, and omitting-and-reporting `Unemittable`/`Dangling`
  fields.
- `SourcePatchApplier.Instances.cs` `RenderAddChildClosure` (:234) passes the edit's `FieldExpressions`
  to `RenderComponentClosureArgs` instead of null, per component.
- The bypass check above (a Core test that every snapshot-emit site omits-and-reports a bare
  non-compiling `Unsupported` and an unmapped `ObjectRef`).

## Editor adapter deliverables

The read path that feeds Defect 3 is Unity-observable: an added child (a GameObject added under a live
prefab instance in the editor) whose component carries a scene reference is read by
`PrefabInstanceProbe` / the snapshot reader into the `AddedGameObjects` shape
`ReconcileAddedGameObjects` consumes. That path is not exercised by any POCO fixture, so it needs an
EditMode test in `unity-gate/Assets/GateTests/` that adds such a child in a real scene and drives a
sync, asserting the emitted source compiles rather than throwing.

## Decomposition guidance

- **Task A (the cross-cutting mechanism, its own owning task).** The classification-routing plus the
  report-channel decision: the bare-`Unsupported` `Unemittable` verdict in `ClassifySnapshotRef`, the
  classification added to `BuildOverrideSetSpec`, and the bypass check that fails if any snapshot-emit
  site renders a value without it. This is the ONE mechanism the three defects share; it owns the check.
  TOUCHES: `SceneBuilder.Core/Reconcile/ComponentReconciler.cs`,
  `SceneBuilder.Core/Reconcile/ReconcilerInstances.cs`, `SceneBuilder.Core/Reconcile/ListValueEmission.cs`
  (if the bare-`Unsupported` predicate lands there), `SceneBuilder.Core.Tests/**` (the bypass and
  omit-and-report tests).
- **Task B (the `FieldExpressions` edit channel).** The new channel on `AppendInstanceAddChild`,
  threaded from `ReconcileAddedGameObjects` through `SnapshotFieldEmission.EmitFieldSet` and out through
  `RenderAddChildClosure`. Depends on Task A (it reuses the same classification/report). TOUCHES:
  `SceneBuilder.Core/Reconcile/SourceEdit.cs`,
  `SceneBuilder.Core/Reconcile/ReconcilerInstances.Nested.cs`,
  `SceneBuilder.Core/Reconcile/SourcePatchApplier.Instances.cs`,
  `unity-gate/Assets/GateTests/**` (the added-child EditMode test),
  `SceneBuilder.Core.Tests/**` (the headless crash-to-handle test).

Keep each task's TOUCHES complete: a task that omits a file it edits mis-scopes the gate.

## Core and adapter test plan

RED tests, behavior not structure:

- **Defect 1, all three sites (Core, headless).** A snapshot component field holding a bare
  `Unsupported` whose `RawToken` is not a compiling expression, driven through `EmitComponentAppend`,
  `BuildAddInstanceComponent` and `BuildOverrideSetSpec`. Assert the field is absent from the emitted
  `Fields`/`ValueExpression` (no raw token), and a `UnrepresentableValue` conflict is reported at each
  site.
- **Defect 2 (Core, headless).** `BuildOverrideSetSpec` with an `ObjectRef` whose target is unmapped.
  Assert no `ValueExpression` naming the target (no phantom identifier) and a `DanglingReference`
  conflict.
- **Defect 3 (Core, headless).** `SourcePatchApplier.Apply` over an `AppendInstanceAddChild` whose
  `Node` component carries an `ObjectRef` field. Assert Apply renders a handle expression and does NOT
  throw `NotSupportedException`.
- **Defect 3 (EditMode, required).** Add a child under a live prefab instance in a real scene, give its
  component a scene reference, and drive a sync. Assert the emitted builder source compiles
  (`BuilderCompileCheck` stays green) and the reference round-trips.

## Unity confirmation checklist

1. In a live scene with a prefab instance, add a child GameObject under the instance and put a
   component with a scene-reference field on it, pointing at another object in the scene. Sync.
   Expected: the builder source gains the `.AddChild(...)` with the reference rendered as a real handle;
   the file compiles; a second sync produces zero edits.
2. Author or read a component whose serialized field the value model cannot represent (a bare
   `Unsupported`). Sync. Expected: the field does not appear in the emitted source, and a located
   conflict names the object, the component type and the field.
3. Read a prefab-instance override whose value is a reference to an object no longer in the scene. Sync.
   Expected: the override is not emitted as a phantom identifier; a located `DanglingReference` is
   surfaced; the file compiles.

## Dependencies

- M2 reconcile (`SourcePatch`, `Conflict`, the located report channel).
- M4/M5 references (`AssetRef`/`ObjectRef` and their handle rendering).
- M6/M10 prefab instances (`AppendInstanceAddChild`, `BuildOverrideSetSpec`, `AddedGameObjects`).
- Spec 33's located report channel and section 7 / section 13 conflict philosophy.

## Risks and notes

- The bare-`Unsupported` verdict must distinguish a token that IS a compiling expression (an escape
  hatch that legitimately round-trips verbatim, section 7) from one that is not. Only the non-compiling
  case is `Unemittable`; a verbatim-round-tripping token stays emittable. The classifier owns that
  distinction so no site re-derives it.
- Task B reuses Task A's classification and report; if Task A's verdict changes, the added-child path
  inherits it. That is the intent of routing every site through one gate rather than patching each.
- The added-child EditMode test is the only site that exercises the real read path feeding Defect 3;
  the headless Core test proves the render/apply half but is structurally blind to the Unity read
  boundary.
