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

- SEVERITY med — every build marks the scene dirty and saves it, whether or not the plan did
  anything. `com.codescenes/Editor/SceneBuilderBuild.cs:241-242` calls
  `EditorSceneManager.MarkSceneDirty(scene)` then `EditorSceneManager.SaveScene(scene, scenePath)`
  inside the suppression scope, with no check on `plan.Ops.Length`, so a converged build that
  reports `0 plan op(s)` still re-saves the scene asset. Auto-sync builds on every debounced change,
  so a scene that already matches its code is rewritten on a keystroke that changed nothing. Found
  while specifying spec 35 D2, whose earlier draft wrongly blamed the phantom plan op for this; the
  op suppression does not touch it. Not measured for user-visible impact — whether the written bytes
  actually differ, and what it costs in editor I/O and version-control noise, is unknown and worth
  measuring before designing a fix. OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY med: an authored prefab-INSTANCE override on an adapter-excluded serialized path never
  converges. It is the same permanent non-convergence trap spec 35 D2 closes for ordinary component
  fields, on a different authoring surface, and D2's fix does not reach it.
  `com.codescenes/Editor/PrefabInstanceProbe.Overrides.cs:91` drops a `PropertyModification` whose
  path `SerializedFieldExclusions.IsExcluded` rejects, so the snapshot's `Overrides` never carries
  it, while `SceneBuilder.Core/Diff/InstanceOverrideDiff.cs:88-100` emits a `SetInstanceOverride`
  for any desired override with no snapshot counterpart. So `.Override(...).Set("m_Size", ...)` on a
  `SpriteRenderer` inside an instance (or `"m_SortingLayer"` on a renderer) emits an op on every
  build, forever, with no report. The sibling sites `InstanceOverrideDiff.cs:138`
  (`AddInstanceComponent`) and `:208` (`AddInstanceChild`) carry whole `ComponentData` payloads and
  have the same exposure. `SceneBuilder.Core/Diff/ExcludedFieldGate.cs` (spec 35 D2) is threaded
  only through `Differ.EmitComponentEdits`; `InstanceOverrideDiff.Emit` is called separately from
  `Differ.WalkDesired:112` and receives no gate. `ExcludedFieldAudit` already scans
  `AddInstanceComponent`/`AddInstanceChild`, so the backstop would flag these ops, but it cannot
  attribute a `SetInstanceOverride` (its `Target` names a nested prefab member, not a model
  component). Not reproduced in a live editor; derived by reading both call sites. Spec 35:98-103
  scopes D2 to the matched field loop and the `AddComponent` branch, so this does NOT relax
  b1-t1's DELIVERABLE. OWNER: unassigned. FOUND-BY: excluded-field-one-way-report.

- SEVERITY med: a stale prefab-instance override detected on the code->scene BUILD is suppressed
  with no report, while the same detection on the scene->code sync is surfaced. Derived by reading
  the chain, not reproduced in a live editor: `SceneBuilder.Core/Diff/Differ.cs:112` calls
  `InstanceOverrideDiff.Emit`, which calls `DetectStaleOverrides`
  (`SceneBuilder.Core/Diff/InstanceOverrideDiff.cs:20,35-63`); the stale key is excluded from the
  Set/Revert emission AND a `ConflictDetector.StaleOverride` conflict
  (`SceneBuilder.Core/Reconcile/ConflictDetector.cs:229-241`, plain object-initializer construction,
  so its `RecurrenceKey` is null) is appended to `ChangeSet.Conflicts` and copied into
  `Plan.Conflicts` (`SceneBuilder.Core/Materialize/Materializer.cs:235`; proven by
  `SceneBuilder.Core.Tests/PrefabInstanceConflictTests.cs:203`). `SceneBuilderBuild.Run` dropped
  `plan.Conflicts` entirely before spec 35 D2, and D2 routes it to
  `ConflictSurfacing.SurfaceNotes`, which skips a null `RecurrenceKey`
  (`com.codescenes/Editor/ConflictSurfacing.cs:161-164`) — so the build still tells the author
  nothing while silently declining to apply their override. The sync direction does surface it
  (`SceneBuilder.Core/Reconcile/ReconcilerInstances.cs:194` -> `ReconcileResult.Conflicts` ->
  `ConflictSurfacing.SurfaceConflicts`, `com.codescenes/Editor/SceneBuilderSync.cs:231`). A fix has
  to decide the recurrence key first: auto-sync builds on every debounced change, so surfacing a
  keyless per-pass conflict on the build path would log on every keystroke. OWNER: unassigned.
  FOUND-BY: excluded-field-one-way-report.

- SEVERITY low — the repo-root walk `private static string RepoRoot([CallerFilePath] string here = "")`
  (walk upward to the directory containing `SceneBuilder.sln`) is duplicated across the Core test
  suite. Measured copies: `SceneBuilder.Core.Tests/ObjectRefDescentScanTests.cs:54`,
  `SceneBuilder.Core.Tests/ListValueEmissionTests.cs:530`,
  `SceneBuilder.Core.Tests/AuthoringBindHarness.cs:17`. Feature `uniform-value-descent` b1-t1 added a
  fourth site as the intended single owner, `SceneBuilder.Core.Tests/RepoRootLocator.cs:12`, and its
  task block scopes migrating the pre-existing copies OUT, so the tree now holds four copies of the
  same walk. New call sites (b1-t2, b4-t2) use the helper; the three old ones do not. Migration is a
  mechanical, behavior-free edit: none of the three files appears in b1-t1's pinned baseline
  (`SceneBuilder.Core.Tests/PinnedTestBaseline.json`), so the pin does not block it. Does not relax
  any DELIVERABLE clause. OWNER: unassigned. FOUND-BY: uniform-value-descent.

- SEVERITY low — the Roslyn source-scan plumbing is duplicated across the two guard tests in the Core
  test suite. `SceneBuilder.Core.Tests/ValueContainerDescentScanTests.cs` reproduces three members
  already present in `SceneBuilder.Core.Tests/ObjectRefDescentScanTests.cs`: `EnclosingMember`
  (`ObjectRefDescentScanTests.cs:132-147` vs `ValueContainerDescentScanTests.cs:145-160`, byte
  identical), `ProductionFiles` (`:71-99` vs `:83-109`, identical but for the exempt relative path)
  and the `DescendantTokens` -> `IdentifierToken` -> previous `DotToken` -> `ValueNode` qualifier
  extraction skeleton (`:104-130` vs `:114-143`, identical but for the matched identifier set).
  Measured on the current tree at validation of `uniform-value-descent` b4-t2. Both members are
  private to their class and `ObjectRefDescentScanTests.cs` is not in b4-t2's declared TOUCHES, so no
  task in that feature could extract the shared helper without an undeclared write. Fix is one new
  internal helper file in `SceneBuilder.Core.Tests/` plus a mechanical edit to both scan tests; it
  changes no assertion. Does not relax any DELIVERABLE clause. OWNER: unassigned.
  FOUND-BY: uniform-value-descent.

- SEVERITY low — two hand-rolled `ValueNode` container descents remain in production because no
  `ValueWalk` primitive expresses their shape. `SceneBuilder.Core/Reconcile/ComponentReconciler.cs:750,766`
  (`AuthoredTextIsCurrent`, 4 tokens) is a PAIRED walk comparing two values position by position and
  every `ValueWalk` primitive takes ONE node; `SceneBuilder.Core/Reconcile/NestedValueEmission.cs:255,256,273,287,290,307`
  (`Complete`, 7 tokens) recurses into `Nested` producing a key set that is the UNION of the value's
  and the default's, which `ValueWalk.Map` cannot express (the production comment at
  `NestedValueEmission.cs:250-252` states this). Measured by the token scan in
  `SceneBuilder.Core.Tests/ValueContainerDescentScanTests.cs`, which ships both as declared inventory
  entries with written reasons. `specs/36-uniform-value-descent.md:283-284` and `:300-304` license a
  declared entry with a reason, so this relaxes no DELIVERABLE clause. Retiring them needs a paired
  `Any` and a union-producing `Map` on `ValueWalk`, a behavior-affecting design change.
  OWNER: unassigned. FOUND-BY: uniform-value-descent.

- SEVERITY low — DANGLING artifact reference inside this register. The repo-root-walk entry above
  cites `SceneBuilder.Core.Tests/PinnedTestBaseline.json` as an existing file ("none of the three
  files appears in b1-t1's pinned baseline (`...PinnedTestBaseline.json`), so the pin does not block
  it"). Measured at validation of `uniform-value-descent` b4-t3: that JSON, plus
  `PinnedTestBaselineTests.cs` and `PinnedBaselineCompletenessTests.cs`, were deleted by b4-t3 (the
  feature-scoped pin's planned retirement), and `docs/open-defects.md` is outside b4-t3's declared
  TOUCHES, so that task could not edit it. The claim the parenthetical supports (the repo-root-walk
  migration is unblocked) remains true and is now unconditional; only the citation points at a file
  that no longer exists. Fix is one prose edit, naturally folded into whoever clears the repo-root
  duplication entry. Does not relax any DELIVERABLE clause. OWNER: unassigned.
  FOUND-BY: uniform-value-descent.

- SEVERITY low — STALE comment misdirecting future fixture authors.
  `unity-gate/Assets/Fixtures/Spatial/SpatialAuthoringExamplesFixture.cs:10-12` states that
  GateFixtures is "deliberately reference-free (BuilderProjectInjectorTests.
  ReferencesAuthoring_ReadsTheRealEditorAssemblyGraph asserts GateFixtures reports no Authoring
  reference)". Both halves are false today, measured at validation of m8-unityevents b1-t0:
  `unity-gate/Assets/Fixtures/GateFixtures.asmdef:4-6` lists `SceneBuilder.Authoring`, and
  `unity-gate/Assets/GateTests/BuilderProjectInjectorTests.cs:293-294` records that GateFixtures "no
  longer serves as the negative case" (the negative case is now `SceneBuilder.Authoring` itself). A
  fixture author reading it would wrongly conclude a fixture needing Authoring cannot live in
  GateFixtures. Fix is one prose edit. Relaxes no DELIVERABLE clause. OWNER: unassigned.
  FOUND-BY: m8-unityevents.

- SEVERITY high — the Unity EditMode layer of `./verify.sh` fails one arbitrary, unrelated test on
  every full run, because Unity emits an engine-level `[Error]` log that the Test Framework attributes
  to whatever test is executing. MEASURED 5/5 full-gate runs: `645 total, 644 passed, 1 failed`, the
  failure always `SetUp : Unhandled log message: '[Error] Unrecognized thread niceness after calling
  setpriority. Target niceness is -6 and actual niceness is 6'` raised inside a fixture's
  `EditorSceneManager.NewScene`
  (`UnrepresentableFieldWarningTests.Sync_AfterWiringAnOnClickInTheInspector_...` in 4 runs,
  `UnqualifiedTypeNameTests.Build_AmbiguousShortName_ThrowsLocatedError` in 1; each fixture passes in
  isolation). The message appears exactly once per editor session. Root cause measured: `ulimit -e`
  (RLIMIT_NICE) is 0 for this user, so no process may lower its nice value (`nice -n -5 true` ->
  "cannot set niceness: Permission denied") and a user session cannot raise the hard limit
  (`systemd-run --user --property=LimitNICE=30` still reports 0); Unity's job system calls
  `setpriority(-6)` once per session when restoring worker-thread priority after an asset-import
  pause. Re-running the identical gate command at nice 0 reproduces it, so the invoking shell's nice
  value is not the cause. Consequence: `./verify.sh` cannot reach `GATE PASS` on this machine, so
  nothing can ship through the gate until it is handled; the handling has to be an explicit, narrow
  exemption for this one engine message (the repo's rule that a skip is not a pass rules out
  `LogAssert.ignoreFailingMessages`). Full evidence:
  `.agent_handoffs/m8-unityevents/b1-t2/gate-output.log`. RECONFIRMED at b1-t2 iteration 2 (run 6 of
  6): same `645 total, 644 passed, 1 failed`, same fixture, `REALEXIT=1`; and the same fixture run
  ALONE in its own editor session (`-testFilter UnrepresentableFieldWarningTests`) is `3 total, 3
  passed, 0 failed` with ZERO occurrences of the niceness message in that session's log, so the test
  content is healthy and only the full-suite session emits the message. OWNER: unassigned.
  FOUND-BY: m8-unityevents.
- SEVERITY med — every consumer of `ValueNode.Primitive.Value` throws on a Primitive that has crossed a
  JSON boundary. `Value` is `object?`, and System.Text.Json materializes it as a boxed
  `System.Text.Json.JsonElement`, which `SceneBuilder.Core/Model/ValueNode.cs:53-70` normalizes for
  equality/hashing and nowhere else. MEASURED with a scratch console against the built
  `SceneBuilder.Core.dll`: round-tripping a `ValueNode.UnityEventListeners` holding one Int-mode
  listener through `CanonicalJson.Serialize`/`Deserialize` yields
  `ArgValue.Value.GetType() == System.Text.Json.JsonElement`; then
  `SceneBuilder.Core/Model/UnityEventProjection.cs:106-109` throws `InvalidCastException: Unable to cast
  object of type 'System.Text.Json.JsonElement' to type 'System.Int32'`, and the pre-existing
  `com.codescenes/Editor/SerializedFieldBridge.cs:458-473` (`Convert.ToInt32(prim.Value)` etc.) throws
  `InvalidCastException: ... to type 'System.IConvertible'`. Not reachable in the shipped product today:
  no `PlanJson.Deserialize` / `SceneModelSerializer.Deserialize` / `CanonicalJson.Deserialize` call site
  exists under `com.codescenes/`, so plan ops are executed as the in-process objects the Materializer
  built. It goes live the moment any pass executes a plan or model read back from disk. The fix belongs
  in one place — a `Primitive` accessor that normalizes by `Kind`, reusing the switch `ValueNode.cs:60-69`
  already has privately — not per call site. OWNER: unassigned. FOUND-BY: m8-unityevents.

- SEVERITY high (CORRECTION to the RLIMIT_NICE entry above, which says the gate fails on EVERY full run
  and "cannot reach GATE PASS on this machine") — MEASURED at b1-t3 validation, two full `./verify.sh`
  runs back to back on the same unchanged tree: run 1 `658 total, 657 passed, 1 failed`, `REALEXIT=1`,
  same charged test and same niceness message; run 2 `658 total, 658 passed, 0 failed`, `REALEXIT=0`,
  `GATE PASS: Core + Unity EditMode green (passed=658 failed=0 skipped=0)`. b1-t0 validation was also
  clean (`637/637`, exit 0). The engine `[Error]` is emitted once per editor session at a
  nondeterministic point and fails the gate only when it lands inside a `[SetUp]`, so the defect is
  intermittent (~half of full runs), not deterministic, and a green gate run IS obtainable. The
  mitigation (a narrow, named exemption in `verify.sh`'s EditMode verdict) is still needed and still
  unowned; only its justification changes. Evidence:
  `.agent_handoffs/m8-unityevents/b1-t3/gate-output.log` (both runs, complete). OWNER: unassigned.
  FOUND-BY: m8-unityevents.

- SEVERITY low — the ordinal-within-type component-key rule (spec 09:31, "Type.FullName +
  ordinal-within-type") is hand-written SIX times, three of them character-identical. MEASURED at
  m8-unityevents b1-t2 validation (`rg -n 'ordinalByType'`, current tree):
  `SceneBuilder.Core/Diff/Differ.cs:380`, `SceneBuilder.Core/Identity/IdentityRemapper.cs:215` and
  `SceneBuilder.Core/Reconcile/ComponentReconciler.cs:806` carry the same
  `ComputeComponentKeys(ComponentData[])` body verbatim; `IdentityRemapper.cs:231`
  (`ComputePriorComponentKeys`), `SceneBuilder.Core/Parsing/BuilderParser.cs:649` and
  `SceneBuilder.Core/Parsing/BuilderParser.Instance.cs:169` re-spell it over different element
  types. The rule DID drift and no test caught it: `ReconcilerInstances.Nested.cs:177-183` composed
  the ARRAY INDEX rather than the ordinal until m8 b1-t2 fixed it, mis-naming the component in a
  user-visible located report (`[Rigidbody, BoxCollider]` anchored `.../BoxCollider#1` where the
  canonical id is `#0`). Only `ComponentReconciler.ComputeComponentKeys` is shared (four callers:
  `ComponentReconciler.cs:65,:97`, `ReconcilerAppends.cs:231`, `ReconcilerInstances.Nested.cs:177`).
  Fix: hoist ONE internal helper (e.g. onto `SceneBuilder.Core/Identity/ComponentTargetResolution.cs`,
  which already owns `ComposeLogicalId`/`TryParseLogicalId`/`OwnerOfLogicalId`) and route all six
  sites through it. `Differ.cs`, `IdentityRemapper.cs`, `BuilderParser.cs` and
  `BuilderParser.Instance.cs` were in no m8 task's TOUCHES, so no task in that run could consolidate
  them; b1-t2 consumed an existing copy rather than adding a seventh. Needs its own task in a run
  whose TOUCHES can hold those four files. OWNER: unassigned. FOUND-BY: m8-unityevents.

- SEVERITY low — three adapter sites still hand-parse the component-LogicalId format
  `"{ownerLogicalId}/{TypeFullName}#{ordinal}"` instead of routing through its Core owner
  `SceneBuilder.Core/Identity/ComponentTargetResolution.cs`. MEASURED at m8-unityevents b1-t2
  validation: `com.codescenes/Editor/InstanceOverrideExecutor.cs:215-221` (`StripOrdinal`, splits on
  the last `/` then the last `#` to recover the bare TYPE token, and its comment still points at
  `PlanExecutor.OwnerOf`, now a one-line delegate to `ComponentTargetResolution.OwnerOfLogicalId`);
  `com.codescenes/Editor/SceneHierarchyPath.cs:72` (`lastSegment.Contains('#')` to decide a
  component id resolves to its owner); `com.codescenes/Editor/PrefabInstanceProbe.cs:122`
  (`sb.Append('#')` composing the ordinal suffix by hand). m8 b1-t2 migrated the four sites its
  deliverable named (`ComponentReconciler.cs`, `ReconcilerInstances.Nested.cs`,
  `ComponentPatchApplier.cs`, `PlanExecutor.cs`) and removed them from the guard's allowlist; these
  three stay allowlisted at
  `SceneBuilder.Core.Tests/ListenerTargetBypassScanTests.cs:190-198`, so the format still has four
  independent spellings and the guard's own header claims a per-entry reason that is not written.
  Two of the three need shapes `ComponentTargetResolution` does not expose today: a LENIENT bare
  type-token accessor (`StripOrdinal`, which must tolerate a missing `#`, unlike the strict
  `TryParseLogicalId`) and a "does this id name a component" predicate. Fix: add those two members
  beside `ComposeLogicalId`/`TryParseLogicalId`/`OwnerOfLogicalId`, migrate all three sites, and
  shrink the allowlist to the owner alone. OWNER: unassigned. FOUND-BY: m8-unityevents.

- SEVERITY low — two stale line-numbered prose pointers into `Reconciler.cs` in the Core test
  suite. MEASURED at m8-unityevents b1-t1 validation, and re-measured against `HEAD` to confirm
  they are PRE-EXISTING (not caused by b1-t1's `DetectRemovals` extraction, which only widened the
  gap): (1) `SceneBuilder.Core.Tests/IdCollisionDataLossTests.cs:14` says "`Reconciler.FlattenModel`
  (Reconciler.cs:952-959)"; at `HEAD` `FlattenModel` was at `Reconciler.cs:939-946` and today it is
  at `:825-832`, with the file 852 lines, so the cited range is past EOF. (2)
  `SceneBuilder.Core.Tests/RectTransformReconcileTests.cs:74` says "Anti-loop rule (mirrors `rot:`,
  Reconciler.cs:792-807)"; at `HEAD` `:792-807` was the middle of `DetectRemovals` and the `rot:`
  anti-loop comment was at `:862`, today it is `Reconciler.MaskDriven` and the `rot:` comment is at
  `:745-760`. Comment text only; both tests pass. This is the third and fourth instance of the same
  rot in one file. Fix: use a type-qualified `Reconciler.<Method>` pointer with no line range, the
  form that survived the move at `SceneBuilder.Core/Diff/Differ.cs:94`,
  `SceneBuilder.Core/Reconcile/ComponentReconciler.cs:10` and
  `SceneBuilder.Core.Tests/ChainedComponentEditTests.cs:603`. No m8 task touches either file.
  OWNER: unassigned. FOUND-BY: m8-unityevents.

- SEVERITY low — two stale prose pointers into a sibling file, in production Core. MEASURED at
  m8-unityevents b1-t1 validation and re-measured against `HEAD` to confirm both are PRE-EXISTING
  (not caused by b1-t1's `DetectRemovals` extraction): (1)
  `SceneBuilder.Core/Reconcile/ReconcilerInstances.cs:165` says "Threaded here from Reconciler.cs";
  the actual caller is `SceneBuilder.Core/Reconcile/ReconcilerAppends.cs:72`, and the appends split
  predates this task (`Reconciler.cs:11-12` is unchanged at `HEAD`). (2)
  `SceneBuilder.Core/Reconcile/SourcePatchApplier.cs:576` says "(ComponentReconciler.cs:390) keeps
  this unreached whenever ParseResult.ChainedComponents"; at `HEAD`, `ComponentReconciler.cs:390` is
  the same dangling-reference-conflict `continue;` it is today, not the REORDER-pass gate the
  sentence describes. Comment text only. Fix: use a type-qualified pointer with no line range, the
  form that survived the move at `SceneBuilder.Core/Diff/Differ.cs:94` and
  `SceneBuilder.Core/Reconcile/ComponentReconciler.cs:10`. No m8 task touches either site's owning
  logic. OWNER: unassigned. FOUND-BY: m8-unityevents.

- SEVERITY low — `SceneBuilder.Core.Tests/UnrepresentableLocatedDataTests.cs` re-lists the
  located-kind set instead of deriving it from `Conflict.RequiresLocatedReport`. MEASURED at
  m8-unityevents b1-t2 validation, current tree: (1) `:148-150` says "`FromReport` is the private
  mechanism it (and AmbiguousTypeName) shares internally"; `FromReport` (`Conflict.cs:140`) is now
  shared by all FOUR located factories (`Conflict.cs:159`, `:171`, `:196`, `:212`). (2) `:175`
  filters the `ConflictDetector` reflection sweep with
  `conflict.Kind != ConflictKind.UnrepresentableValue`, so a future `ConflictDetector` factory
  producing `UnauthorableField` or `UnsyncableListener` is skipped silently. LATENT today, not a
  live hole: `ConflictDetector` sets only `AmbiguousAnchor` directly (`:89`) and builds every
  located report through `Conflict.Unrepresentable` (`:281,297,312,325`), so the filter is
  currently equivalent to `!RequiresLocatedReport`; the broader guard
  `AmbiguousShortNameReportTests.Conflict_EveryPublicFactoryProducingALocatedKind_CarriesTheLocatedData`
  (`:93-118`) already derives its set from the rule and covers all four kinds. Fix: apply the same
  move here, derive from `Conflict.RequiresLocatedReport` rather than enumerate. The file is in no
  m8 task's TOUCHES, so no task in that run could hold it. OWNER: unassigned. FOUND-BY:
  m8-unityevents.

- SEVERITY low — b1-t2's widening of the component-closure sub-grammar also opened a SECOND, untested
  surface: the prefab-instance `.AddComponent<T>(c => ...)` closure. MEASURED at b1-t2 validation on the
  current tree by reading both call graphs: `BuilderParser.Instance.cs:322` (`ApplyAddComponent`) and
  `FlatShapeRecognizer.Instance.cs:305` both call the SAME `ProcessComponentClosure` that b1-t2 widened, and
  `com.codescenes/Runtime/InstanceHandle.cs:57,134` + `ScopedHandle.cs:30` type the config lambda as
  `Action<ComponentHandle<T>>`, which now carries `OnClick<TTarget>`. So
  `inst.AddComponent<Button>(c => c.OnClick(opener, o => o.Open()));` COMPILES, RECOGNIZES with zero
  violations, and PARSES into `NodeBuilder.AddedComponents[i].Fields["m_OnClick"]` today. Parser and
  recognizer widened together, so there is no mirror divergence; the hole is that no test covers the path and
  no m8 task declares it. If b2/b3 materialize listeners only off `NodeBuilder.Components`, an authored
  `.OnClick` on a prefab-instance added component is a silent no-op. Fix: either cover the
  `AddedComponents` path in materialization or reject `.OnClick` in `ApplyAddComponent`'s closure on both
  mirror sides. LATENT until listener materialization ships.
  OWNER: unassigned — needs a task that can hold `BuilderParser.Instance.cs` +
  `FlatShapeRecognizer.Instance.cs` (or the listener materializer). FOUND-BY: m8-unityevents.

- SEVERITY high — a `UnityEngine.UI.Slider` authored via the generic `Component<Slider>` path drifts
  on scene->code round-trip: the live RectTransform materialized for a plain Slider carries
  anchoredPosition/sizeDelta/anchorMin/anchorMax/pivot values the unauthored source does not, so the
  first sync over a converged build emits 5 `PatchArgument` edits on those RectTransform members and the
  scene never reaches a fixed point (5 phantom patches on every sync — fatal to seamless sync for any
  Slider-bearing scene). Measured by the m8 EditMode test
  `Item7_SliderOnValueChangedDynamicToHudSetValue_SourceIsOnEventDynamic_RoundTrips`, which fails at
  `RoundTripProofHarness.cs:273` ("produced 5 patch edit(s); it must be a fixed point"); the dynamic
  OnEvent listener itself round-trips cleanly (assertScene and the code->scene fixed-point passes all
  pass), so the drift is purely RectTransform-side, not UnityEvents. Owning territory is spec 13
  (RectTransform). No listener path touches RectTransform, so this predates the m8 UnityEvents work.
  OWNER: unassigned (needs a dedicated task). FOUND-BY: m8-unityevents-remaining.

- SEVERITY low — a `[SerializeReference]` field ABSENT from builder source but non-null in the live
  scene cannot be synced scene->code: there is no introduce/remove `.SetRef` applier (only
  `IntroduceComponentField`, which emits `.Set(...)`, the wrong call for a managed ref). The b1-t3
  reconcile intercept (`SceneBuilder.Core/Reconcile/ComponentReconciler.ManagedRef.cs:45-51`)
  deliberately declines the absent-from-source case (returns false, falls through to existing generic
  logic) rather than emit a `.Set(...)` managed ref. Not exercised by the M9 checklist/fixtures
  (b2-t5 authors the field first, so source always holds it); no M9 deliverable is relaxed. A future
  `SetRef`-introduce path would close it. OWNER: unassigned. FOUND-BY: m9-serializereference.

- SEVERITY low — a scene/asset reference held inside a PLAIN `[Serializable]` nested struct that is
  itself a field of a managed instance (e.g. `new Aggressive { data = new Payload { target = handle } }`)
  resolves in NEITHER direction. code->scene: `ManagedReferenceWriter` delegates a plain-`Nested` child
  to `SerializedFieldBridge.WriteField` -> `WriteProperty`/`EnterNode`, whose `ObjectRef`/`AssetRef`
  handling is a deliberate no-op (SerializedFieldBridge.cs:494-502). scene->code (emit): the plain-nested
  member falls to `SourceExpr.ValueNodeLiteral` -> `NestedValueEmission.IsRepresentable`, which excludes
  `ObjectRef`/`AssetRef` (NestedValueEmission.cs:229-236). b2-t4's fix resolves refs that are DIRECT
  fields of a managed instance and refs inside nested MANAGED refs (recursively) via
  `ManagedReferenceEmission`/`ManagedReferenceWriter`; the fix's own justification does not read
  identically for a plain-[Serializable]-struct child (a different write/emit mechanism), so it is not a
  half-applied sibling. Spec 10 lists "nested" as an in-scope managed-instance field kind, so this is a
  real latent gap; NOT exercised by M9 fixtures (Aggressive/Flee/Composite hold only leaves, refs, and
  nested managed refs), no M9 deliverable relaxed. Closing it needs the shared write descent
  (`WriteProperty`) and emit descent (`IsRepresentable`) to carry ref-resolution context.
  OWNER: unassigned. FOUND-BY: m9-serializereference.

- SEVERITY low — `SourceExpr.ValueNodeLiteral` emit of a `ValueNode.List` of `ManagedReference`
  renders `new object[] { new Aggressive{…}, new Flee{…} }` (`ListValueEmission.EmittedTypeToken` has
  no `ManagedReference` arm -> null -> `object[]` prefix); the List node carries no shared
  base-interface type, so Core cannot name the correct element type from the model alone. This
  round-trips at the model level (reparse yields an equal `List` of `ManagedReference`), but
  `new object[]{…}` will NOT compile if emitted into real authoring against a typed
  `[SerializeReference] List<IStrategy>` / `IStrategy[]` field (CLAUDE.md: emitted C# must compile).
  NOT exercised by spec 10's numbered Unity checklist (items 1-7 all target the single `strategy`
  field; none authors or emits a managed-ref LIST), so no M9 deliverable is relaxed — b2-t5's EditMode
  round-trip deliverable is met without it. Was recorded OWNER b2-t5 in the gitignored
  `.agent_handoffs/m9-serializereference/tasks.md` DEFECTS register; b2-t5 closed GREEN as a test-only
  task without touching the emit path, so it is re-homed here (repo-tracked) to survive the run.
  Closing it needs the List node to carry a shared element type, or `EmittedTypeToken` to gain a
  `ManagedReference` arm. OWNER: unassigned. FOUND-BY: m9-serializereference.

- SEVERITY low (cosmetic) — a `[SerializeReference]` managed instance's null object-ref fields are
  rendered EXPLICITLY on sync as `target = NodeHandle.None.As<UnityEngine.GameObject>()` even when the
  author never wrote the field. The reconciler's whole-value-span rewrite re-renders the entire
  `new T { ... }` initializer including a null/default object-ref member, so a clean
  `new Aggressive { range = 5f }` becomes `new Aggressive { range = 5f, target =
  NodeHandle.None.As<UnityEngine.GameObject>() }` after the first sync. MEASURED during M9 live-verify
  to be a STABLE FIXED POINT (re-build converges then 0 ops, re-sync 0 patch edits, source byte-stable
  thereafter), so it is unauthored verbosity, not round-trip churn. Fix direction: omit a null/default
  object-ref (and likely asset-ref) member when rendering a `ManagedReference`/`Nested` initializer,
  the same omit-at-default rule the top-level field path uses. OWNER: unassigned. FOUND-BY:
  m9-serializereference (live-verify).

- SEVERITY low (spec self-inconsistency) — specs/39-prefab-authoring.md:188-200 (Authoring API
  example: root.Name(...)/root.Component<>() called directly on the PrefabRoot param and
  root.Add=child) contradicts the same spec's normative parser statement at 39:204-209 + 39:216-220
  ("statement grammar is unchanged; recognition is the only parse change"). The richer example needs
  new grammar the decomposition forbids. b1-t2 follows the normative grammar-unchanged text; the
  Name/Component-on-root sugar is NOT delivered. Reconcile the spec (drop the sugar from the example,
  or spec a new grammar milestone). OWNER: unassigned. FOUND-BY: prefab-authoring (b1-t2).

- SEVERITY low (mirror drift) — SceneBuilder.Grammar/FlatShapeRecognizer.Discovery.cs:22-23
  (TryFindBuildMethod) hard-codes "ISceneDefinition" and was NOT widened for IPrefabDefinition (nor, since nested-prefabs-and-variants b1-t1 widened FindBuildMethod for IPrefabVariantDefinition, for variants) when
  BuilderParser.FindBuildMethod (SceneBuilder.Core/Parsing/BuilderParser.cs:129-131) was. Build-time
  parsing is unaffected; the CodeScenes analyzer's IDE diagnostics recognize a prefab builder only via
  its single-Build-method fallback (:38-50), so a prefab file with multiple Build methods gets no
  in-IDE recognition. Out of spec-39 (no analyzer scope). OWNER: unassigned. FOUND-BY: prefab-authoring (b1-t2).

