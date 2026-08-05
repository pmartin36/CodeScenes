# Open defects

Measured defects with no owning task. Each entry: severity, the concrete observation, and the
feature whose run found it. Entries are removed only when the fix ships with a regression test.

- SEVERITY med — a component authored in SOURCE but carrying no `Kind = "Component"` IdentityMap
  entry is emitted TWICE: the mapped-owner ADD pass
  (`SceneBuilder.Core/Reconcile/ComponentReconciler.cs:108-151`) emits an
  `AppendComponentStatement` while the field-value diff pass (`:219-423`) emits an
  `IntroduceComponentField` for the same component, so the applied source authors the component
  twice and the next Build adds a second live component. Measured with a plain `int` field (so it
  is unrelated to references and fully pre-existing): edits
  `AppendComponentStatement, IntroduceComponentField`, emitted source
  `opener.Component<Game.DoorOpener>(c => c.Set("speed", 7));` followed by
  `opener.Component<Game.DoorOpener>(c => { c.Set("speed", 7); });`.
  `ComponentReconciler.cs:84-88` shows the overlap is known and guarded for the ASSET harvest only
  ("edit emission is unaffected"). Reachability in a live editor was not confirmed; the window is a
  scene->code sync that runs before the code->scene build that would register the entry. No task in
  this plan touches this pass. OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY med — a BARE `ValueNode.Unsupported` snapshot field value is emitted as its raw marker
  token into builder source, silently, at three sites: `error CS0103` on the next compile, no
  conflict, no note, no `SkippedField`. `SourceExpr.ValueNodeLiteral`
  (`SceneBuilder.Core/Reconcile/SourceExpr.cs:105`) renders `ValueNode.Unsupported =>
  unsupported.RawToken` by design, and only the field-VALUE-DIFF pass guards against reaching it
  (`SceneBuilder.Core/Reconcile/ComponentReconciler.cs:229-240`, which records a `SkippedField` and
  skips). Rule B (`ListValueEmission.HasUnemittableItem`) deliberately does NOT cover the bare case
  — pinned by `SceneBuilder.Core.Tests/ListValueEmissionTests.cs:450`
  (`HasUnemittableItem_BareUnsupported_NotInsideAList_IsFalse`). Measured on the current tree with a
  read-only out-of-tree probe over the public `Reconciler.Reconcile`:
  (a) `ComponentReconciler.EmitComponentAppend` — applied source
  `opener.Component<Game.DoorOpener>(c => c.Set("targets", ObjectReference));`,
  `edits=1 conflicts=0 notes=0 skipped=0`;
  (b) `ReconcilerInstances.BuildAddInstanceComponent` — `Fields=[targets] FieldExpressions=<null>`,
  same token at apply time;
  (c) `ReconcilerInstances.BuildOverrideSetSpec` — `SET Game.DoorOpener.target
  ValueExpression=ObjectReference`.
  Reachable from a real scene: `AssetReferenceResolver.ReadObjectReferenceValue`
  (`com.codescenes/Editor/AssetReferenceResolver.cs:473-487`) returns
  `Unsupported("ObjectReference")` whenever the identity resolver cannot map a scene object, and
  `SerializedFieldBridge.ReadComponent` feeds that straight into an added/appended component's field
  set. Pre-existing at HEAD (`git show HEAD:SceneBuilder.Core/Reconcile/SourceExpr.cs` carries the
  same arm at line 105; HEAD's `BuildAddInstanceComponent` has no veto either), so it is not a
  regression of this run. Fixing it is a contract choice — which of the two existing report channels
  (`Skipped` vs a located `UnrepresentableValue` note) owns the bare case, applied at all three
  sites at once — so it wants a task of its own rather than a localized patch.
  CLAUDE.md rates a write path that can emit non-compiling source a bug outright.
  OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY med — `ReconcilerInstances.BuildOverrideSetSpec`
  (`SceneBuilder.Core/Reconcile/ReconcilerInstances.cs:435-446`) renders an override whose
  `ObjectReference` names a DANGLING or PENDING target as a phantom identifier, with no report. It
  calls `ComponentReconciler.RenderFieldValue` (`:445`) with no classification at all — the only
  emit site left that does. Measured on the current tree (read-only out-of-tree probe over the public
  `Reconciler.Reconcile`, matched `PrefabInstanceNode`, snapshot-only `PropertyOverride` with
  `ObjectReference = ObjectRef("ghost-1")`): dangling target ->
  `SET Game.DoorOpener.target ValueExpression=ghost-1`, `edits=1 conflicts=0 notes=0`; pending target
  (an extra snapshot root whose GlobalObjectId is `ghost-1`, no IdentityMap entry) -> identical
  expression, `edits=2 conflicts=0 notes=0`. `ghost-1` is not a C# identifier: `error CS0103` on the
  next compile. Pre-existing at HEAD, not a regression of this run —
  `git show HEAD:SceneBuilder.Core/Reconcile/ComponentReconciler.cs` shows `RenderFieldValue`
  returning `handle ?? targetLogicalId` for a bare `ObjectRef` there too. Not fixed by b1-t1: the fix
  is a contract choice (omit the whole `.Set` from the `Override(e => ...)` chain and report, vs.
  keep it), which no planned task's DELIVERABLE holds — b3-t2 touches this function but only for the
  `member:` sigil question. OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY med — an added GameObject's component fields are rendered at APPLY time through
  `SourceExpr.ValueNodeLiteral`, which has no `ObjectRef` arm, so a child added inside a prefab
  instance whose component carries a scene reference crashes the sync.
  `SourcePatchApplier.Instances.cs:191-211` (`RenderAddChildClosure` ->
  `RenderComponentClosureArgs(c.Fields, null)`) passes `null` for the field-expression dictionary,
  and `AppendInstanceAddChild` (`SceneBuilder.Core/Reconcile/SourceEdit.cs:330`) has no
  `FieldExpressions` channel to carry a pre-rendered handle at all. Measured on the current tree
  (read-only out-of-tree probe calling the public `SourcePatchApplier.Apply` with an
  `AppendInstanceAddChild` whose `Node` holds one component): field `ObjectRef("ghost-1")` ->
  `THREW NotSupportedException: SourceExpr.ValueNodeLiteral: unsupported ValueNode kind ObjectRef`;
  field `List[ObjectRef("ghost-1")]` -> same throw. Loud rather than silent, so it is a crash and not
  a bad write, but the whole sync pass dies. Reached from
  `Reconciler.ReconcileAddedGameObjects` (`ReconcilerInstances.Nested.cs:84-145`), which builds the
  edit straight from `snapshotAdded.Node` with no reference classification; live-editor reachability
  follows the same `SerializedFieldBridge.ReadComponent` read path as every other component field but
  was not confirmed in an editor. Fixing it means a new edit shape (a `FieldExpressions` channel on
  `AppendInstanceAddChild`), not a classification call, so no planned task's DELIVERABLE holds it.
  OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY med — every debounced auto-sync cycle already performs a FULL cold scene assemble, so the
  O(changed) incremental cache never survives more than one cycle.
  `SceneBuilderAutoSync.CaptureBaseline` (`com.codescenes/Editor/SceneBuilderAutoSync.cs:628`) calls
  `assembler.AssembleCold` on the SAME per-builder assembler instance returned by `GetAssembler`, and it
  is called at the tail of every cycle body: `ExecuteSceneToCode:502`, `ExecuteCodeToScene:583`,
  `ExecuteBothChanged:698`. `AssembleCold` (`ChangeScopedSnapshot.cs:30-31, :37, :48`) does
  `Ids.Clear()`, `Ids.WarmBatch(CollectAllGameObjects(scene))` (a whole-scene `GetGlobalObjectIdsSlow`
  batch) and a `SceneSnapshotReader.ReadNode` over every root — a full `SerializedObject` read of every
  component in the scene — then replaces `_nodeByGoEntityId` wholesale. So the incremental assemble at
  `:495` is always served by a cache the immediately-preceding cold assemble built, and the per-keystroke
  cost is a full-scene walk regardless. Derived by construction from the call chain, not timed. CLAUDE.md
  rates a full-scene walk per keystroke fatal rather than a tradeoff. Not fixable inside b2-t1: the
  baseline is a whole-scene converged snapshot read under the POST-sync sidecar, so reusing the
  pre-sync incremental result is a contract change to `_baselines`, not a localized edit.
  OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY med — the scene-ref resolver bypasses `GlobalObjectIdCache` and calls
  `GlobalObjectId.GetGlobalObjectIdSlow` uncached, once per assigned scene-reference FIELD, on every read.
  `ObjectReferenceResolver.BuildSceneRefResolver`'s returned closure
  (`com.codescenes/Editor/ObjectReferenceResolver.cs:185`) resolves the target's goid on every invocation;
  it is invoked from `AssetReferenceResolver.ReadObjectReferenceValue`
  (`com.codescenes/Editor/AssetReferenceResolver.cs:484`) for every non-asset object reference, on both the
  cold and incremental read paths. `ChangeScopedSnapshot.Ids` (the `GlobalObjectIdCache` that exists
  precisely to make this O(changed)) is threaded to node identity only — never to the ref resolver — so
  these calls are neither cached nor counted. Consequence: the repo's O(changed) perf gate
  (`AutoIdentityTests.Identity_SingleFieldEdit_ResolutionCountProportionalToChangeSet`, asserting
  `css.Ids.ResolutionCount == 1`) is structurally blind to them; a scene of N objects each holding a
  reference field costs N uncached slow resolves per cold assemble on top of the counted ones. Derived by
  construction, not timed. Fixing it means threading the cache into the resolver plus deciding its
  invalidation for a moved/reparented target, which no planned task's DELIVERABLE holds.
  OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY low — `SceneBuilder.Core.Tests/SpatialComponentTests.cs` is 1083 lines, over the
  1000-line file-size budget this plan states (measured: `wc -l`; it is the only file in the tree
  over the limit, next highest is `unity-gate/Assets/GateTests/RoundTripSpatialTests.cs` at 979 and
  `SceneBuilder.Core/Reconcile/SourcePatchApplier.cs` at 941). Consequence for this run: b1-t1's
  budget assertion (deliverable 5) is scoped to production source (`SceneBuilder.Core/**`,
  `com.codescenes/**`) and does NOT cover test files, because a repo-wide assertion would be red on
  landing. OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY low — `specs/00-foundation.md:210-212` lists the Plan op vocabulary and states
  "(`SetReference(path,target)` for cross-object refs is **forthcoming** — M5-pending, not yet
  emitted.)". Measured false: `SceneBuilder.Core/Materialize/Materializer.cs:289` emits
  `SetReference` and has since M5, and the same list omits `InstantiatePrefab` and the ten
  instance-override ops registered at `SceneBuilder.Core/Plan/PlanOp.cs:21-31`. A reader treating
  §5 step 4 as the op registry gets a wrong answer. OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY low — `ReconcileResult.Skipped` is logged once per skipped field on EVERY sync:
  `com.codescenes/Editor/SceneBuilderSync.cs:237` does `Debug.LogWarning` per entry, and
  `SceneBuilderAutoSync.cs:497` calls `SceneBuilderSync.Run` on every debounced change. A scene
  holding one field the reader could not represent (`ComponentReconciler.cs:229-240` adds a
  `SkippedField` unconditionally, before the source-vs-snapshot equality check) therefore prints a
  console warning on every keystroke-driven sync, forever. The `Notes` channel next to it
  (`Conflict.RecurrenceKey` -> `ConflictSurfacing.SurfaceNotes`) exists precisely to surface a
  standing condition once per editor session; `Skipped` has no such de-duplication. OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY med — a newly-appended ROOT GameObject's declaration is always seated AFTER every
  existing root statement, regardless of its live scene sibling index, so a later field-value patch
  that introduces a reference TO it from an EARLIER existing statement emits a forward reference and
  the written source fails to compile. `Reconciler.DetectAppends`
  (`SceneBuilder.Core/Reconcile/ReconcilerAppends.cs:126-138`) seats a root-level append at
  `nextIndexByParentKey` defaulting to `expected.Roots.Length` (after every already-authored root),
  never at the live root sibling index the append's own doc comment claims to use (`:61-62`, "the
  array position IS the scene sibling index" — true only for the recursion order, not the seat
  index). Measured on the current tree: a converged `Opener` (`opener.Component<DoorOpener>()`,
  field unauthored) plus a hand-created root `Door` wired as `Opener`'s live target — reordering
  `Door` to live sibling index 0 via `Transform.SetSiblingIndex(0)` before syncing made no
  difference to its emitted position. `SceneBuilderSync.Run` -> `BuilderCompileCheck.CheckAndReport`
  (`com.codescenes/Editor/BuilderCompileCheck.cs:286`) reports `CS0841: Cannot use local variable
  'door' before it is declared` against `opener.Component<DoorOpener>(c => c.Set("target", door));`
  preceding `var door = scene.Add("Door");`. A CHILD append does not hit this (seated immediately
  after its parent's own declaration via `StatementPlacement.PlacementIndex`,
  `SceneBuilder.Core/Reconcile/StatementPlacement.cs:224-229`), which is why the b2-t2 gate test
  parents its hand-created reference target under the referencing object instead of at scene root.
  Fixing it means either seating a root append at its live sibling index among ALL roots (not just
  `expected.Roots.Length`) or deferring the field-introducing patch until the target's declaration
  precedes it in text — a Reconciler/StatementPlacement contract change, not a localized patch.
  OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY low — four gate fixture headers and one test header state that
  `SceneBuilderRouter.Discover()` is TypeCache-backed and routes only "a REAL compiled type":
  `unity-gate/Assets/Fixtures/AutoSceneToCodeScene.cs`, `AutoCodeToSceneScene.cs`,
  `AutoIntegrationScene.cs`, `AutoConflictScene.cs`, and `unity-gate/Assets/GateTests/AutoSceneToCodeTests.cs:22-24`
  ("a temp-dir seed is invisible to TypeCache and would silently no-op ExecuteSceneToCode"). Measured
  false: `com.codescenes/Editor/SceneBuilderRouter.cs:61-92` is a plain
  `Directory.GetFiles(SceneBuilderPaths.BuildersDirectory, "*.cs")` scan, its own class doc at `:43-49`
  says builders "are never compiled, so a Unity type index cannot see them — the `.cs` file IS the unit",
  and `unity-gate/Assets/GateTests/MultiSceneRoutingTests.cs:127-145`
  (`MultiScene_Discover_EnumeratesOutOfAssetsBuilders`) pins discovery of a builder with NO compiled
  counterpart anywhere in the domain. Consequence: a test author following the comment adds an
  unnecessary compiled `ISceneDefinition` fixture under `Assets/`, which costs a domain reload on the
  gate and implies a constraint the product does not have. Comments only; no observable behavior
  changes. OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY low — spec 35 D3's "Reachable sequence, every step an ordinary UI action with auto-sync
  armed" (`specs/35-reference-writes-and-cache-invalidation.md:104-113`) never says how its step-1
  precondition arises — a scene object that a MAPPED object references but the IdentityMap does not
  know — and that precondition does not survive hand-creating the target while auto is armed. Derived
  by construction: creating the object publishes `CreateGameObjectHierarchy` and wiring the field
  publishes `ChangeGameObjectOrComponentProperties`, both handled at
  `com.codescenes/Editor/SceneBuilderAutoSync.cs:232-247` -> `NotifySceneChanged` (`:287`) -> a
  scene->code cycle `SettleSeconds = 0.4` later (`:45-46`, `:328-369`), whose `DetectAppends` maps the
  target with its live GlobalObjectId (`SceneBuilder.Core/Reconcile/ReconcilerAppends.cs:215-221`).
  So under armed auto the raw-goid window is bounded by one 0.4 s debounce. Scene LOAD publishes none
  of the handled `ObjectChangeKind`s (`:230-266`), so a scene opened from disk that ALREADY contains
  such an object holds the precondition indefinitely — that is the durable reachable state, and it is
  the state b2-t2's EditMode test models implicitly (it never wires the executors, so no cycle ever
  runs). Consequence: a reader of the spec can conclude the sequence is reachable within 0.4 s of any
  hand edit, and a later test author can build a scenario that self-heals before step 2. Does NOT
  relax b2-t3's DELIVERABLE: b2-t3's live pass reproduces the durable case explicitly (scene saved,
  closed and reopened from disk with the target already wired). OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY low — three gate tests blanket-suppress the emitted-source compile check around a real
  sync, for a reason the product has since fixed, so a non-compiling emission in those tests is
  invisible. `SyncIgnoringFacadeCompileNoise` in
  `unity-gate/Assets/GateTests/NestedTypedEmitTests.cs:88-100`,
  `unity-gate/Assets/GateTests/NestedRoundTripTests.cs:104-116` and
  `unity-gate/Assets/GateTests/TypedInstanceOverrideSyncTests.cs:92-104` sets
  `LogAssert.ignoreFailingMessages = true` around `SceneBuilderSync.Run`, which swallows the
  `Debug.LogError` that `BuilderCompileCheck.CheckAndReport`
  (`com.codescenes/Editor/BuilderCompileCheck.cs:286`, called at
  `com.codescenes/Editor/SceneBuilderSync.cs:287`) raises for a `DOES NOT COMPILE` emission; those
  three files also bypass the `EmittedCodeCompiles.SyncAndAssertCompiles` seam
  (`unity-gate/Assets/GateTests/EmittedCodeCompiles.cs:44`), so nothing else asserts it there. The
  stated cause is stale: `TypedInstanceOverrideSyncTests.cs:88-90` says "Bug 5 (facade CS0103
  compile-noise) is a separate task (b5) not yet fixed", and b5 shipped —
  `com.codescenes/Editor/PrefabFacadeStubEmitter.cs` feeds `BuilderCompileCheck` a `Prefabs` stub
  precisely so facade authoring binds. Measured on the current tree: with the three suppressions
  removed (`= true` -> `= prevIgnore`) an isolated batchmode EditMode run of those three classes is
  `<test-run result="Passed" total="5" passed="5" failed="0" skipped="0">`; the suppressions were
  restored afterwards. Not measured: a FULL-suite run without them, where cross-test manifest state
  differs. Consequence: `EmittedCodeCompiles.cs:20-22`'s "a future test cannot silently skip it by
  forgetting to opt in" is false for these three, and the compile class of bug CLAUDE.md calls a bug
  outright can land there unseen. OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY low - two archived spec listings document a signature the tree no longer has after this
  feature's D4 widening. `specs/completed/19-spatial-authoring-components.md:300` prints
  `NodeHandle target = null);` for `SurfaceSnap`, and
  `specs/completed/06-m5-cross-object-references.md:129` prints
  ``**`ComponentHandle<T>.Set<TValue>(Func<T,TValue> selector, NodeHandle target)`**``. Both
  parameters are `SceneObjectHandle` on the current tree (`com.codescenes/Runtime/NodeHandle.cs:67`,
  `com.codescenes/Runtime/ComponentHandle.cs:34`). Re-measured by tdd-validator during b3-t1
  iteration 2 (`sed -n '298,302p'` / `sed -n '127,131p'`): both lines still print `NodeHandle`.
  Dead prose in an archive; nothing observable changes. Raised as a b3-t1 finding by
  `scope/bucket-b3.md` and NOT fixed by the routed iteration, which addressed the guard-completeness
  finding only; `specs/completed/` is in no task's TOUCHES in this plan. OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.
