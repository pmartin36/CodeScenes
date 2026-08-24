# Open defects

Measured defects with no owning task. Each entry: severity, the concrete observation, and the
feature whose run found it. Entries are removed only when the fix ships with a regression test.

- SEVERITY low — `ReconcileResult.Skipped` is logged once per skipped field on EVERY sync:
  `com.codescenes/Editor/SceneBuilderSync.cs:237` does `Debug.LogWarning` per entry, and
  `SceneBuilderAutoSync.cs:497` calls `SceneBuilderSync.Run` on every debounced change. A scene
  holding one field the reader could not represent (`ComponentReconciler.cs:229-240` adds a
  `SkippedField` unconditionally, before the source-vs-snapshot equality check) therefore prints a
  console warning on every keystroke-driven sync, forever. The `Notes` channel next to it
  (`Conflict.RecurrenceKey` -> `ConflictSurfacing.SurfaceNotes`) exists precisely to surface a
  standing condition once per editor session; `Skipped` has no such de-duplication. OWNER: unassigned. FOUND-BY: reference-writes-and-cache-invalidation.

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

- SEVERITY low — a benign code->scene build skip is logged at ERROR severity. When the pump routes a
  code->scene cycle while the target scene is not open, `SceneBuilderAutoSync.ExecuteCodeToScene`
  (`com.codescenes/Editor/SceneBuilderAutoSync.cs`, the skip-guard around `:671`) emits
  `[CodeScenes] <builder>: scene ... is not open — code->scene build skipped` at Error level, so a
  non-error condition surfaces as a red console Error. Observed once during spec-42 live-verify (a
  fixture builder written before its scene was opened); fires once, not per-keystroke. Pre-existing;
  unrelated to `0c4feff`. Fix: log the skip at Info/Warning, not Error. OWNER: unassigned. FOUND-BY:
  spec-42 live-verify.

- SEVERITY med — prefab-instance override closure re-renders a typed selector for an inaccessible
  member. `.Override(e => e.Set(x => x.priv, v))` re-renders via RenderOverrideSetCall's `member:`
  arm (SceneBuilder.Core/Reconcile/SourcePatchApplier.Instances.cs:288-291) as a typed selector
  `(T x) => x.<name>`, the same non-compiling class (CS0122) as spec-46 defect-1 but on the
  prefab-instance override path, which spec-46's accept-when (a plain component field) does not
  exercise. syncback-emits-compiling-code b1-t1 fixes only the component `.Set(...)` closure in
  ComponentPatchApplier; the override render path is a distinct mechanism and was not covered. Fix
  direction: apply the same inaccessible-member downgrade at the override render site. OWNER:
  unassigned. FOUND-BY: syncback-emits-compiling-code (b1-t1 research).

- SEVERITY low — no self-heal of an authored inaccessible typed selector without a scene change. An
  authored inaccessible typed selector in an already-converged builder produces no
  PatchComponentField (SceneBuilder.Core/Reconcile/ComponentReconciler.cs:415-420 fires only on a
  value diff), so sync never rewrites it and BuilderCompileCheck stays red for that file. b1-t1's
  value-change-driven downgrade satisfies spec-46's "field set from the scene" accept-when but does
  not heal the unchanged case; a diff-independent source normalizer would. OWNER: unassigned.
  FOUND-BY: syncback-emits-compiling-code (b1-t1 research).

- SEVERITY med — a forward-reference builder never reaches a true reconcile fixed point; every
  re-sync logs a Convergence defect Error. When a statement references a handle declared later, spec
  46(b)'s declare-before-use hoist (StatementPlacement) moves the `var` declaration ABOVE its use, so
  source statement order no longer matches scene sibling order. The sibling-order reorder pass then
  emits ReorderStatement edits every sync to force source order back to scene order, but applying them
  would re-break declare-before-use, so placement keeps the declaration hoisted and the edits apply
  byte-IDENTICALLY forever. Measured shape on a converged builder (Alpha at sibling 0 references Beta
  at sibling 1, `var beta` hoisted above Alpha): two no-op edits per sync,
  `ReorderStatement{Anchor=beta,NewSiblingIndex=1}` + `ReorderStatement{Anchor=Alpha,NewSiblingIndex=0}`.
  The pre-existing convergence guard (SceneBuilderSync.cs:314, added 322e143, predating spec-46)
  suppresses the re-apply so the file stays byte-stable, but emits `[SceneBuilder] Convergence defect:
  reconcile produced N patch edit(s) that applied byte-identically` at Error severity on EVERY sync of
  any builder with this shape — the natural "add A, later add B, A references B" case, so B has the
  higher sibling index. Reproduces on an isolated fixture, not demo-scene drift; making B's sibling
  index precede A's converges immediately (PatchEdits=0). CAUSED BY spec 46(b): the hoist traded the
  CS0841 compile error for this perpetual no-op reorder. The file-level byte-stability that satisfies
  spec-46 accept-when (c) is delivered by suppression, not true convergence. Spec-46's gate test
  SyncBackCompilesRoundTripTests dodges the shape via door.SetSiblingIndex(2), so both the offline Core
  and EditMode suites are blind to it. Owner: StatementPlacement + the sibling-order reorder pass
  (SceneBuilder.Core/Reconcile/). Fix direction (either): the declare-before-use hoist carries the
  referenced statement's sibling-order position so declaration order can lead sibling order without the
  reorder pass fighting it, OR the reorder pass treats a hoist-forced ordering as its converged target
  and stops emitting reorders it can never apply. Add a Core round-trip test over a forward-ref fixture
  where the referrer's sibling index < the handle's, asserting PatchEdits == 0 on re-sync. OWNER:
  unassigned. FOUND-BY: spec-46 live-verify.
