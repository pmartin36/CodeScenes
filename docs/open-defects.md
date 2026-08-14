# Open defects

Measured defects with no owning task. Each entry: severity, the concrete observation, and the
feature whose run found it. Entries are removed only when the fix ships with a regression test.

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

- SEVERITY low — `ReconcileResult.Skipped` is logged once per skipped field on EVERY sync:
  `com.codescenes/Editor/SceneBuilderSync.cs:237` does `Debug.LogWarning` per entry, and
  `SceneBuilderAutoSync.cs:497` calls `SceneBuilderSync.Run` on every debounced change. A scene
  holding one field the reader could not represent (`ComponentReconciler.cs:229-240` adds a
  `SkippedField` unconditionally, before the source-vs-snapshot equality check) therefore prints a
  console warning on every keystroke-driven sync, forever. The `Notes` channel next to it
  (`Conflict.RecurrenceKey` -> `ConflictSurfacing.SurfaceNotes`) exists precisely to surface a
  standing condition once per editor session; `Skipped` has no such de-duplication. OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

- SEVERITY med — a scene reference from an EARLIER-declared object to a LATER-declared root emits a
  forward reference and the written builder source fails to compile (CS0841). REPRODUCED LIVE (two manual
  syncs, auto off): a converged `Opener` (`var opener = scene.Add("Opener"); opener.Component<Linker>();`,
  Linker.target unauthored); create a new ROOT `Door` at end-of-roots (Opener@0, Door@1) and wire
  `Opener.Linker.target = Door`. Sync 1 appends `scene.Add("Door");` after Opener and silently omits the
  pending target. Sync 2 (Door mapped) emits `opener.Component<Linker>(c => c.Set("target", door));` on
  line 9 BEFORE `var door = scene.Add("Door");` on line 10 -> `SceneBuilderSync.Run(...).CompileErrors` =
  `CS0841`, raised at `com.codescenes/Editor/BuilderCompileCheck.cs:286`.
  REFUTED PREMISE (this entry previously blamed a mis-seated append): the append is seated at its live
  sibling index via `AppendStatement.NewSiblingIndex = siblingIndex` (`SceneBuilder.Core/Reconcile/
  ReconcilerAppends.cs:203`); the `expected.Roots.Length` value (`:129-138`) feeds only
  `LogicalIdResolver.Synthesize` (`:179`), never the seat. Measured: sync 1 seated Door at index 1
  correctly, and reordering Door to sibling 0 DID move its declaration to the top and compiled
  (PatchEdits=4, 0 errors). So "seat at live sibling index" is a no-op.
  REAL CAUSE: the reference-introducing patch is folded IN PLACE into the anchor's existing statement.
  `ComponentReconciler` emits `IntroduceComponentField` (`ComponentReconciler.cs:468`/`:519`) applied by
  `ComponentPatchApplier.ResolveIntroduceComponentField` as an in-place rewrite of the anchor's existing
  `.Component<T>(...)` call (`ComponentPatchApplier.cs:233`), never routing through `StatementPlacement`
  or checking that a referenced handle is declared earlier. When the referrer's sibling index precedes
  the target's (a normal layout), the target's `var` is legitimately later and the patch forward-refs it.
  `StatementPlacement.MinIndexAfterReceiverDeclaration` compounds it: its floor is receiver-only, ignoring
  handles used as ARGUMENTS.
  FIX DIRECTION: defer the reference assignment into a SEPARATE statement seated after the target's
  declaration (a new deferred-field-assignment emission shape), or generalize the placement floor to the
  max declaration index over ALL handles a statement names, not just the receiver. A scene reorder is NOT
  a fix (the target belongs at its later sibling). Reconciler/StatementPlacement contract change, so it
  wants its own spec + RED test (headless two-pass Core sim asserting the applied source compiles, plus an
  EditMode two-sync gate test whose reference target is a scene ROOT, not a child of the referrer).
  OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.
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

- SEVERITY low (mirror drift) — SceneBuilder.Grammar/FlatShapeRecognizer.Discovery.cs:22-23
  (TryFindBuildMethod) hard-codes "ISceneDefinition" and was NOT widened for IPrefabDefinition (nor, since nested-prefabs-and-variants b1-t1 widened FindBuildMethod for IPrefabVariantDefinition, for variants) when
  BuilderParser.FindBuildMethod (SceneBuilder.Core/Parsing/BuilderParser.cs:129-131) was. Build-time
  parsing is unaffected; the CodeScenes analyzer's IDE diagnostics recognize a prefab builder only via
  its single-Build-method fallback (:38-50), so a prefab file with multiple Build methods gets no
  in-IDE recognition. Out of spec-39 (no analyzer scope). OWNER: unassigned. FOUND-BY: prefab-authoring (b1-t2).
