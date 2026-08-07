# M8 (specs/09 UnityEvents) — measured defects register

Extracted 2026-08-06 from `.agent_handoffs/m8-unityevents/tasks.md` before that plan was regenerated
from scratch. Every entry below was MEASURED against real code or a real editor during M8's bucket-b1
implementation run; none is a guess. Kept in the repo because `.agent_handoffs/` is gitignored and
rediscovering these costs live editor runs.

Reading notes for whoever picks this up:
- Entries marked RESOLVED name the commit that fixed them. Do not re-derive those.
- Entries whose OWNER names a task id refer to the PRE-REGENERATION plan; those ids no longer exist.
  Treat the measurement as true and the ownership as void.
- Bucket b1 shipped in `30a9613`; anything describing b1's files as unwritten is stale.

## DEFECTS

- SEVERITY med — **RESOLVED 2026-08-06 in commit `10d8fd5`; do NOT act on the text below.** The spec now
  reads `0 Off, 1 EditorAndRuntime, 2 RuntimeOnly`, matching the measurement. Retained for its evidence.
  (superseded) OWNER: b1-t3 — `UnityEventCallState`'s numeric values in spec 09:277 (and in b1-t3's
  ASSUMPTIONS line) are wrong. MEASURED against the shipped editor's
  `~/Unity/Hub/Editor/6000.5.3f1/Editor/Data/Managed/UnityEngine/UnityEngine.CoreModule.dll` (read via
  `MetadataLoadContext`, raw constant values): `UnityEngine.Events.UnityEventCallState` is
  `Off = 0, EditorAndRuntime = 1, RuntimeOnly = 2`. Spec 09:277 states `0 Off, 1 RuntimeOnly,
  2 EditorAndRuntime` — RuntimeOnly and EditorAndRuntime are transposed. `PersistentListenerMode`
  in the same measurement DOES match spec 09:275-277 (`EventDefined = 0, Void = 1, Object = 2,
  Int = 3, Float = 4, String = 5, Bool = 6`). b1-t3 (d) already owns pinning these in the gate; this
  entry exists so the wrong number is not transcribed from the spec first.

- SEVERITY low — OWNER: b4-t2 — b4-t2 (f) requires the patched source to compile ("the Roslyn compile
  assertion in the gate stays green"), but the Core-side harness for that assertion is
  `SceneBuilder.Core.Tests/AuthoringBindHarness.cs`, whose only stub set (`DoorOpenerStubs`, :56-71)
  declares `Game.DoorOpener` + `UnityEngine.GameObject`/`Material`/`Prefabs` and no Button, no
  `UnityEvent`, no listener-target type. `AuthoringBindHarness.cs` appears in no task's TOUCHES, so a
  reconcile test that binds emitted `.OnClick(...)`/`.OnEvent(...)` source has to extend a file its task
  does not declare. Either add it to b4-t2's TOUCHES or state that the compile proof for this task rides
  the editor-side `BuilderCompileCheck` (which compiles against the loaded assembly set, GateFixtures
  included) instead.

- SEVERITY low — OWNER: unassigned — stale comment.
  `unity-gate/Assets/Fixtures/Spatial/SpatialAuthoringExamplesFixture.cs:10-12` states that GateFixtures
  is "deliberately reference-free (BuilderProjectInjectorTests.ReferencesAuthoring_ReadsTheRealEditorAssemblyGraph
  asserts GateFixtures reports no Authoring reference)". Both halves are false today:
  `unity-gate/Assets/Fixtures/GateFixtures.asmdef:4-6` lists `SceneBuilder.Authoring`, and
  `unity-gate/Assets/GateTests/BuilderProjectInjectorTests.cs:293-294` records that GateFixtures "no
  longer serves as the negative case" (the negative case is now `SceneBuilder.Authoring` itself). The
  comment misdirects any future fixture author deciding where to put a fixture that needs Authoring.

- SEVERITY med — OWNER: b4-t2 — the SOURCE-RENDER path for `ValueNode.UnityEventListeners` has no owning
  TOUCHES. MEASURED: `ComponentReconciler.RenderFieldValue` (`SceneBuilder.Core/Reconcile/ComponentReconciler.cs:585-619`)
  substitutes EVERY `ObjectRef` it reaches with `ValueNode.Unsupported(<rendered token>)` and hands the
  substituted tree to `SourceExpr.ValueNodeLiteral` (`SceneBuilder.Core/Reconcile/SourceExpr.cs:72-81`),
  which folds containers and throws `NotSupportedException` on a kind with no arm (`:117`). The same
  renderer is the appended-component/appended-object path via `ComponentPatchApplier.cs:139,204` and
  `SourceEdit.cs:160-239`. Neither `SourceExpr.cs` nor `ComponentPatchApplier.cs` appears in any task's
  TOUCHES, yet b4-t2 (a)/(g2) and b4-t3 both drive a listener value down it. b1-t1 supplies the Fold arm
  as a THROW naming the required route, so the gap fails loud instead of rendering
  `Set("m_OnClick", ...)`, but b4-t2 must declare `SceneBuilder.Core/Reconcile/SourceExpr.cs` (and the
  appended-component render site) in its TOUCHES and route the kind to `.OnClick`/`.OnEvent`.

- SEVERITY high — OWNER: b4-t2 — a listener `Target` carrying a COMPONENT LogicalId is classified
  DANGLING by the shipped reconcile classifier, so every listener the milestone produces would report a
  conflict and emit no patch. MEASURED: `SceneBuilder.Core/Reconcile/Reconciler.cs:437-451` builds
  `resolvableTargets` from `sceneLiveTargets` (`IdentityNodeIndex.IsMappedNode` — GameObject /
  PrefabInstance ONLY) UNION `modelByLogicalId.Keys` (GameObject nodes) UNION `handles.Keys` (authored
  `var` names for nodes). No component LogicalId is a member of any of the three.
  `ComponentReconciler.ClassifySnapshotRef` (`ComponentReconciler.cs:502-533`) then walks every ObjectRef
  reached in the value — after b1-t1 that includes a listener's `Target`/`ArgValue` — and returns
  `Dangling` for any target absent from `resolvableTargets` and from `pendingTargets`. b4-t2 must add
  component identities to `resolvableTargets` (and to the handle table for the render), not just dispatch
  on the new value kind.

- SEVERITY med — OWNER: b4-t3 — §13's "converges on a guaranteed second Sync" does not hold for a
  COMPONENT listener target. MEASURED: `Reconciler.cs:459-461` builds `pendingTargets` from
  `snapshotByGoid.Keys`, and `snapshotByGoid` is keyed on GAMEOBJECT GlobalObjectIds
  (`SceneSnapshotReader.cs:20-21` stamps nodes). A component's own GlobalObjectId is therefore never in
  `pendingTargets`, so an unmapped component target falls to `Dangling` (permanent conflict) instead of
  `Pending` (defer + converge).

- SEVERITY med — OWNER: b4-t1 — the incremental-snapshot node cache can serve a STALE listener target.
  MEASURED: `com.codescenes/Editor/ChangeScopedSnapshot.cs:65,53,130` keys its per-GameObject node cache
  on `SceneRefResolver.Generation`, which is `IdentityNodeIndex.MappedNodeGeneration`
  (`SceneRefResolver.cs:33-36`, `IdentityNodeIndex.cs:36-42`) — a content key over MAPPED NODES only.
  A sync that only adds/retargets `Kind=="Component"` entries leaves that key unchanged, so a cached
  node keeps the listener target it read under the older map. b1-t2 ships
  `ComponentTargetIndex.Generation` (node + mapped-component projections); b4-t1 must key the read cache
  on it and declare `com.codescenes/Editor/SceneRefResolver.cs` and
  `com.codescenes/Editor/ChangeScopedSnapshot.cs` in its TOUCHES — neither file is in any task's TOUCHES
  today.

- SEVERITY low — OWNER: unassigned — `SceneBuilder.Core/Reconcile/ReconcilerInstances.Nested.cs:180`
  composes a component LogicalId as `$"{instanceLogicalId}/{component.Type.FullName}#{i}"` where `i` is
  the index in `node.Components`, NOT the ordinal-within-type every other site uses
  (`BuilderParser.cs:648-655`, `ComponentReconciler.cs:678`). For components `[Rigidbody, BoxCollider]`
  it yields `.../BoxCollider#1` where the canonical id is `.../BoxCollider#0`. The value is used only as
  the conflict-report key handed to `ComponentDefaultOmission.OmitDefaults`, so the damage is a
  mis-identified report rather than a wrong patch.

- SEVERITY low — OWNER: unassigned — the component-LogicalId format has six shipped sites outside the
  owner b1-t2 introduces, none of them in an M8 task's TOUCHES: compose at
  `SceneBuilder.Core/Reconcile/ReconcilerInstances.Nested.cs:180` and
  `SceneBuilder.Core/Reconcile/ComponentReconciler.cs:678`; parse at
  `SceneBuilder.Core/Reconcile/ComponentPatchApplier.cs:371`, `com.codescenes/Editor/PlanExecutor.cs:375`
  (`OwnerOf`), `com.codescenes/Editor/InstanceOverrideExecutor.cs:214` (`StripOrdinal`) and
  `com.codescenes/Editor/SceneHierarchyPath.cs:72`. b1-t2 routes the two `BuilderParser` compose sites and
  guards the rest with a per-file scan allowlist, so a NEW site fails; consolidating the existing six is a
  behaviour-free refactor with no owning task.

- SEVERITY low — OWNER: unassigned — four test files under `SceneBuilder.Core.Tests/` each carry a private
  copy of the "enumerate production .cs under SceneBuilder.Core + com.codescenes, skipping obj/bin" walk:
  `ObjectRefDescentScanTests.cs:59-80`, `ValueContainerDescentScanTests.cs:80-108`, the new
  `ListenerTargetBypassScanTests.cs`, and b1-t3's planned `ModeArgBypassScanTests.cs`. `RepoRootLocator`
  was already extracted for the root walk; the file enumeration was not.

- SEVERITY high — **RESOLVED 2026-08-06 in commit `69a74df`, no task needed. Do NOT re-derive this.**
  `verify.sh:202-234` now discounts this exact engine message when it is a failure's SOLE cause, prints
  what it discounted, keeps any other `[Error]` failing, and accepts `unity_exit=2` only when every
  failure was exempt (a crash or license failure still fails — proved live by an editor SIGABRT, exit
  134, failing loudly with no results.xml). Verified after the fix:
  `GATE PASS: Core + Unity EditMode green (passed=657 failed=0 skipped=0)`. Original diagnosis retained
  below because it is correct and explains the exemption's justification —
  the Unity EditMode layer of `./verify.sh` fails one arbitrary, unrelated test on every
  full run of this machine, because Unity emits an engine-level `[Error]` log the Test Framework
  attributes to whatever test is executing. MEASURED at b1-t2 validation, 5/5 full-gate runs (3 by the
  code-writer, 2 by the validator): `645 total, 644 passed, 1 failed`, the failure always
  `SetUp : Unhandled log message: '[Error] Unrecognized thread niceness after calling setpriority.
  Target niceness is -6 and actual niceness is 6'` raised inside a fixture's
  `EditorSceneManager.NewScene`. The poisoned test was
  `UnrepresentableFieldWarningTests.Sync_AfterWiringAnOnClickInTheInspector_...` in 4 runs and
  `UnqualifiedTypeNameTests.Build_AmbiguousShortName_ThrowsLocatedError` in 1; each fixture passes
  clean when run in isolation, and the message appears exactly ONCE per editor session
  (`grep -c` on `unity-gate/editor.log` = 1). Root cause measured: `ulimit -e` (RLIMIT_NICE) is 0 for
  this user, so no process may lower its nice value (`nice -n -5 true` -> "cannot set niceness:
  Permission denied"), and a user session cannot raise the hard limit
  (`systemd-run --user --property=LimitNICE=30` still reports 0). Unity's job system calls
  `setpriority(-6)` once per session when restoring worker-thread priority after an asset-import
  pause; the call cannot reach the target and the engine logs the error. Re-running the identical
  gate command at nice 0 (`systemd-run --user --nice=0 ./verify.sh`) reproduces it identically, so the
  agent shell's own nice value is NOT the cause. Consequence: no task in this feature can reach a
  quoted `GATE PASS` until this is handled, and every validator loop burns a ~4-minute editor run to
  rediscover it. The fix is a repo-level decision (a defined handling in `verify.sh` or in the gate
  suite for this known engine-environment message) that no current task's TOUCHES holds — the repo's
  own rule that a skip is not a pass means it must be an explicit, narrow exemption, not
  `LogAssert.ignoreFailingMessages`. RECONFIRMED at b1-t2 validation iteration 2 (run 6 of 6, same
  `645 total, 644 passed, 1 failed`, same fixture, `REALEXIT=1`), and NARROWED: the charged fixture run
  ALONE in its own editor session (`-testFilter UnrepresentableFieldWarningTests`) is `3 total, 3
  passed, 0 failed`, unity exit 0, with ZERO occurrences of the message in that session's log — so the
  test content is healthy, this is not a stale test, and the defect is purely the full-suite session's
  engine log poisoning the gate verdict.
  RE-MEASURED INDEPENDENTLY at b1-t2 research iteration 2, run 7 of 7, and TWO candidate mitigations are
  now RULED OUT by measurement rather than by argument: (1) `-job-worker-count 0` on the full EditMode
  suite still gives `645 total, 644 passed, 1 failed`, `UNITY_EXIT=2`, message count 1, same charged
  test; (2) the message is emitted at log line 121977 of 122629, AFTER 2750 `Asset Pipeline Refresh`
  entries (2612 `StopAssetImportingV2`) — it is an END-of-session event, not the first asset refresh, so
  a `[SetUpFixture]` prewarm cannot relocate it out of the tests. Host limits re-measured directly:
  `ulimit -e` = 0, `prlimit --nice` soft 0 / hard 0, `nice -n -5 true` -> Permission denied. Permitting
  `setpriority(-6)` requires `RLIMIT_NICE` >= 26 (allowed nice floor = `20 - RLIMIT_NICE`), which only
  root can grant. b1-t2 research returned STATUS: BLOCKED on this entry: the two live options are a
  root-level host fix (PAM `nice` limit + `DefaultLimitNICE` in systemd system.conf AND user.conf, then
  re-login) or a new task owning `TOUCHES: [verify.sh, docs/open-defects.md]` that adds a narrow
  exact-message exemption to the EditMode verdict. Both are human decisions; no agent can take either
  from inside a feature task.

- SEVERITY med — OWNER: b3-t1 — spec 09:176-180 lists the typed `UnityEventTools.Add*PersistentListener`
  family FIRST among the two write mechanisms, and b3-t1/b3-t2's ASSUMPTIONS treat it as a live option.
  MEASURED at b1-t3 research by reading the shipped editor's
  `~/Unity/Hub/Editor/6000.5.3f1/Editor/Data/Managed/UnityEditor.dll` metadata directly: every member of
  that family takes a TYPED delegate the adapter would have to synthesize for an arbitrary reflected
  method — `AddVoidPersistentListener(UnityEventBase, UnityAction)`,
  `AddIntPersistentListener(UnityEventBase, UnityAction<int>, int)`, likewise Float/String/Bool, and
  `AddObjectPersistentListener<T>(UnityEventBase, UnityAction<T>, T)` which is additionally GENERIC
  (needs MakeGenericMethod). There is NO Add* overload for the dynamic (EventDefined, mode 0) case at
  all, which spec 09:201-203 and b3-t2 both require. The only mode-agnostic member is
  `AddPersistentListener(UnityEventBase)`, which appends an EMPTY call. So the typed family cannot cover
  the matrix on its own; the realistic mechanism is `m_PersistentCalls.m_Calls` through
  `SerializedObject` (optionally seeded by the parameterless `AddPersistentListener`), which is also the
  mechanism b1-t3's total `PersistentCallFields` record is shaped for. Recorded so b3-t1 measures rather
  than re-deriving, and so the choice is not made by whichever of b3-t1/b3-t2 runs first.

- SEVERITY low — OWNER: unassigned — the false line in the SPEC itself is still unowned.
  `specs/09-m8-unityevents.md:277` states `UnityEventCallState`: `0 Off, 1 RuntimeOnly,
  2 EditorAndRuntime`. INDEPENDENTLY RE-MEASURED at b1-t3 research (raw constant values read from
  `~/Unity/Hub/Editor/6000.5.3f1/Editor/Data/Managed/UnityEngine/UnityEngine.CoreModule.dll` via
  `System.Reflection.Metadata`): `Off = 0, EditorAndRuntime = 1, RuntimeOnly = 2`. The existing med
  entry above assigns b1-t3 the CODE side (Core's table uses the measured numbers, pinned in the gate by
  `UnityEventEnumPinTests`), but no task's TOUCHES includes `specs/`, so the wrong number stays in the
  authoritative spec for the next reader. `PersistentListenerMode` in the same measurement matches
  spec 09:275-277 exactly (`EventDefined 0, Void 1, Object 2, Int 3, Float 4, String 5, Bool 6`), as do
  the serialized field names spec 09:144-146 lists (`PersistentCall`: `m_Target`,
  `m_TargetAssemblyTypeName`, `m_MethodName`, `m_Mode`, `m_Arguments`, `m_CallState`; `ArgumentCache`:
  `m_ObjectArgument`, `m_ObjectArgumentAssemblyTypeName`, `m_IntArgument`, `m_FloatArgument`,
  `m_StringArgument`, `m_BoolArgument`; `PersistentCallGroup.m_Calls`;
  `UnityEventBase.m_PersistentCalls`).

- SEVERITY high — **ALSO RESOLVED by commit `69a74df`; OWNER: none, do NOT mint a task.** This entry was
  written from b1-t3 validation, which PREDATES that commit (`69a74df` landed 2026-08-06 20:47), so its
  "still needs a task of its own" is stale by construction. Both this and the entry above describe the one
  gate flake, and the exemption in `verify.sh:202-234` covers it: verified after the fix at
  `GATE PASS: Core + Unity EditMode green (passed=657 failed=0 skipped=0)`. Left in place rather than
  deleted because its measurements are the justification for the exemption. Original text follows.
  (superseded) OWNER: unassigned (still needs a
  task of its own) — MEASURED at b1-t3 validation, 2 full `./verify.sh` runs back to back on the same
  unchanged tree: run 1 `658 total, 657 passed, 1 failed`, `REALEXIT=1`, charged test
  `UnrepresentableFieldWarningTests.Sync_AfterWiringAnOnClickInTheInspector_...` (identical message and
  stack as every prior occurrence); run 2 `658 total, 658 passed, 0 failed`, `REALEXIT=0`,
  `GATE PASS: Core + Unity EditMode green (passed=658 failed=0 skipped=0)`. The b1-t3 code-writer's own
  full run was likewise clean, and b1-t0 validation was `637/637`, exit 0. So the register's claim that
  the message poisons a test on EVERY full run, and that `./verify.sh` "cannot reach GATE PASS on this
  machine", is WRONG: the engine `[Error]` is emitted once per session at a nondeterministic point and
  only fails the gate when it lands inside a `[SetUp]`. Practical effect is unchanged — roughly every
  other full-gate run is red for a reason no task owns — but the mitigation task must be justified as
  "intermittent, ~50% of runs", not "deterministic". Evidence:
  `.agent_handoffs/m8-unityevents/b1-t3/gate-output.log` (both runs, complete).

- SEVERITY med — OWNER: unassigned — every consumer of `ValueNode.Primitive.Value` throws on a
  Primitive that has crossed a JSON boundary. `Value` is `object?`, and System.Text.Json materializes it
  as a boxed `System.Text.Json.JsonElement`, which `ValueNode.cs:53-70` normalizes for equality/hashing
  but nowhere else. MEASURED with a scratch console against the built
  `SceneBuilder.Core.dll`: `CanonicalJson.Deserialize<ValueNode>(CanonicalJson.Serialize<ValueNode>(
  UnityEventListeners([Int listener with ArgValue=Primitive.Int(7)])))` yields
  `ArgValue.Value.GetType() == System.Text.Json.JsonElement`, and then
  (1) `SceneBuilder.Core/Model/UnityEventProjection.cs:106-109` throws
  `InvalidCastException: Unable to cast object of type 'System.Text.Json.JsonElement' to type
  'System.Int32'` (its own doc comment and b1-t3's contract call `ToPersistentCall` total/no-throw), and
  (2) the pre-existing `com.codescenes/Editor/SerializedFieldBridge.cs:458-473`
  (`Convert.ToInt32(prim.Value)` etc.) throws
  `InvalidCastException: Unable to cast object of type 'System.Text.Json.JsonElement' to type
  'System.IConvertible'`. NOT reachable in the shipped product today: no `PlanJson.Deserialize` /
  `SceneModelSerializer.Deserialize` / `CanonicalJson.Deserialize` call site exists under
  `com.codescenes/` (verified by grep), so plan ops are always executed as the in-process objects the
  Materializer built. It becomes live the moment any pass executes a plan or model it read back from
  disk — including a natural b3-t1 test that round-trips a `SetUnityEvent` op through plan JSON and then
  applies the deserialized op. Fix belongs in one place (a `Primitive` accessor that normalizes by
  `Kind`, the same switch `ValueNode.cs:60-69` already has privately), not per call site.
  FOUND-BY: m8-unityevents (b1-t3 validation).

STATUS: READY


## Appended from the second plan (reroll), extracted 2026-08-07

Same reading rules as above: measurements true, task-id ownership void.


Feature-level register of real defects measured during this run that the measuring task must not fix.

- SEVERITY med — `SceneBuilder.Core/Reconcile/SourcePatchApplier.cs` is 941 lines (`wc -l`,
  re-measured b1-t1 iteration 1) against the 1000-line budget enforced at
  `SceneBuilder.Core.Tests/ObjectRefDescentScanTests.cs:242` — 59 lines of headroom. No split task
  exists for it (b1-t1 splits `Reconciler.cs` only, and `SourcePatchApplier.cs` is outside b1-t1's
  TOUCHES). b4-t3 edits it and self-imposes "at most a dispatch hook" (deliverable (h)) but carries
  no ASSUMPTION or escalation path for the case where that hook plus its wiring exceeds 59 lines,
  which would land the file over the gate mid-task. Same finding as plan-review.md MED-3, which
  remains unrouted: plan-review.md is not read by the task agents, so this register is its channel.
  OWNER: b4-t3 — attack it as an assumption at research time (measure the real hook size before
  writing it; if it does not fit, split `SourcePatchApplier.cs` first rather than growing it).

- SEVERITY low — a `ComponentData` reached ONLY through a prefab-instance channel carries an EMPTY
  `GlobalObjectId` after b1-t2 (a). MEASURED: b1-t2 populates the component goid at the general
  snapshot read (`com.codescenes/Editor/SceneSnapshotReader.cs:181-183`, the one site that builds
  `SnapshotNode.Components`); the two OTHER `SerializedFieldBridge.ReadComponent` production call
  sites — `com.codescenes/Editor/PrefabInstanceProbe.Overrides.cs:288` (an instance's
  `AddedComponents[]`) and `com.codescenes/Editor/PrefabInstanceProbe.Nested.cs:267` (an instance's
  `AddedGameObjects[]` node components) — do not stamp one and are outside b1-t2's TOUCHES. A
  listener whose target is an added component ON a prefab instance therefore has no goid in the
  snapshot, so it cannot enter the PENDING set and falls to Dangling.
  OWNER: b4-t4 — it owns the pending-target path that consumes the goid; decide there whether the
  instance channels are in scope for §13 convergence or an explicitly stated boundary.

- SEVERITY low — the new `ConflictKind.UnsyncableListener` report surfaces as a console NOTE, not a
  WARNING. MEASURED: `com.codescenes/Editor/ConflictSurfacing.cs:183-186` labels only
  `UnrepresentableValue` and `UnauthorableField` as WARNING and everything else as NOTE. A listener
  the user wired in the Inspector that is NOT reaching their code is the same user-visible class of
  event as `UnrepresentableValue` (spec 09:196-197 calls it fail-loud), so it should read WARNING.
  `ConflictSurfacing.cs` is in no M8 task's TOUCHES.
  OWNER: b4-t1 — the first task that constructs the report from the scene->code direction.

- SEVERITY med — `SceneBuilder.Core.Tests/ModeArgBypassScanTests.cs` does not guard what
  `tasks.md:241-242` claims ("`ModeArgBypassScanTests` fails the adapter if it grows a mode table of
  its own"). MEASURED in the current tree (b1-t2 research iteration 3): Scan A's allowlist
  (`:78-84`, PINNED at `:222-231`) is exactly `{SceneBuilder.Core/Model/UnityEventProjection.cs,
  com.codescenes/Editor/UnityEventWriter.cs, com.codescenes/Editor/UnityEventReader.cs}`, and
  `ls com.codescenes/Editor/UnityEvent*` returns no matches — both adapter entries are reserved
  names for files that do not exist yet. Scan B (`:236-247`) fires only on the identifiers
  `ListenerArgMode` / `ListenerCallState`, which a hand-written raw-integer mode/arg table would
  never name. So a mode table written inside `UnityEventWriter.cs` or `UnityEventReader.cs` is
  invisible to BOTH scans and spec 09:219-220 ("Adapter carries no mode/arg logic beyond calling the
  typed API Core selects") is unguarded exactly where the adapter listener code lands. b1-t2 is the
  wrong owner — shrinking an allowlist for two files that do not exist guards nothing.
  `ModeArgBypassScanTests.cs` is in no M8 task's TOUCHES, so widening is required.
  OWNER: b3-t1 — it creates `UnityEventWriter.cs`, the first real allowlisted adapter file
  (b4-t1 creates the second, `UnityEventReader.cs`). Same finding as
  `scope/bucket-b1.md` finding 1 and `plan-review.md` MED-1.

- SEVERITY low — the ordinal-within-type component-key rule (spec 09:31, "Type.FullName +
  ordinal-within-type") is hand-written SIX times, three of them character-identical. MEASURED
  (b1-t2 research iteration 4, `rg -n 'ordinalByType'`):
  `SceneBuilder.Core/Diff/Differ.cs:380`, `SceneBuilder.Core/Identity/IdentityRemapper.cs:215` and
  `SceneBuilder.Core/Reconcile/ComponentReconciler.cs:804` are the same `ComputeComponentKeys(
  ComponentData[])` body verbatim; `IdentityRemapper.cs:231` (`ComputePriorComponentKeys`),
  `SceneBuilder.Core/Parsing/BuilderParser.cs:649` and
  `SceneBuilder.Core/Parsing/BuilderParser.Instance.cs:169` re-spell it over different element
  types. The rule DID drift: `ReconcilerInstances.Nested.cs:177-183` used the array INDEX instead
  of the ordinal until b1-t2 fixed it, and no test caught it. Only
  `ComponentReconciler.ComputeComponentKeys` is shared (four callers:
  `ComponentReconciler.cs:65,:97`, `ReconcilerAppends.cs:231`,
  `ReconcilerInstances.Nested.cs:177`). `Differ.cs`, `IdentityRemapper.cs`, `BuilderParser.cs` and
  `BuilderParser.Instance.cs` are in NO M8 task's TOUCHES, so no task in this run can consolidate
  them; b1-t2 consumed an existing copy rather than adding a seventh.
  OWNER: unassigned — needs its own task (hoist one internal helper, e.g. onto
  `ComponentTargetResolution`, and route all six sites through it) in a run whose TOUCHES can hold
  those four files.

- SEVERITY low — two more stale `Reconciler.cs:<line>` prose pointers in the Core test suite, both
  PRE-EXISTING (measured wrong against `HEAD` too, so NOT caused by b1-t1's move — it only widened
  the gap). MEASURED b1-t1 iteration 2: (1)
  `SceneBuilder.Core.Tests/IdCollisionDataLossTests.cs:14` says "`Reconciler.FlattenModel`
  (Reconciler.cs:952-959)"; `Reconciler.cs` is 852 lines, so that range is past EOF, and
  `FlattenModel` is at `Reconciler.cs:825-832`. (2)
  `SceneBuilder.Core.Tests/RectTransformReconcileTests.cs:74` says "Anti-loop rule (mirrors `rot:`,
  Reconciler.cs:792-807)"; `:792-807` is now `Reconciler.MaskDriven` and the `rot:` anti-loop rule is
  the comment at `Reconciler.cs:745-760`. Comment text only; both tests pass. This is the third and
  fourth instance of the same rot in one file — the durable form is a type-qualified
  `Reconciler.<Method>` pointer with no line range, which is what survived the move at `Differ.cs:94`,
  `ComponentReconciler.cs:10` and `ChainedComponentEditTests.cs:603`.
  OWNER: unassigned — no M8 task touches either file, and b1-t1 is one routed finding wide.

- SEVERITY low — two stale prose pointers in PRODUCTION Core, both MEASURED pre-existing at `HEAD`
  (b1-t1 research iteration 3), so b1-t1 does not own them and its comment-repair diff deliberately
  leaves both untouched. (1) `SceneBuilder.Core/Reconcile/ReconcilerInstances.cs:165` says the facade
  catalog is "Threaded here from Reconciler.cs"; the actual caller of `HandleInstanceNode` is
  `SceneBuilder.Core/Reconcile/ReconcilerAppends.cs:72`, and that appends split predates M8
  (`Reconciler.cs:11-12` is unchanged at `HEAD`). (2)
  `SceneBuilder.Core/Reconcile/SourcePatchApplier.cs:576` calls `ComponentReconciler.cs:390` the
  "REORDER-pass gate"; `git show HEAD:SceneBuilder.Core/Reconcile/ComponentReconciler.cs | sed -n
  '390p'` is a `continue;` inside a dangling-reference conflict arm, the same as today, so the
  pointer was already wrong before M8 and b1-t1 made no line-shifting edit above `:390` in that file.
  Comment text only; no behaviour, no assertion. This is the fifth and sixth site of the same rot.
  Fix: type-qualified `<Type>.<Member>` with no file name and no line range, the form that survived
  the move at `Differ.cs:94`, `ComponentReconciler.cs:10`, `ChainedComponentEditTests.cs:361`.
  OWNER: unassigned — no M8 task touches either region.

- SEVERITY low — `SceneBuilder.Core.Tests/UnrepresentableLocatedDataTests.cs` re-lists the
  located-kind set instead of deriving it from `Conflict.RequiresLocatedReport`, in the same two
  places the routed b1-t2 iteration-4 finding names. MEASURED (b1-t2 research iteration 5): (1)
  `:148-150` says "FromReport is the private mechanism it (and AmbiguousTypeName) shares internally";
  `FromReport` (`Conflict.cs:141`) is now shared by all FOUR located factories (`Conflict.cs:160`,
  `:172`, `:197`, `:213`). (2) `:175` filters the `ConflictDetector` sweep with
  `conflict.Kind != ConflictKind.UnrepresentableValue`, so a future `ConflictDetector` factory
  producing `UnauthorableField` or `UnsyncableListener` is skipped silently. LATENT today, not a live
  hole: `rg -n 'Kind = ConflictKind\.' ConflictDetector.cs` yields only `AmbiguousAnchor` (`:89`) and
  every located report it builds goes through `Conflict.Unrepresentable` (`:281,297,312,325`), so the
  filter is currently equivalent to `!RequiresLocatedReport`. Fix is the move already applied at
  `AmbiguousShortNameReportTests.cs:90,108`: derive from the rule, do not enumerate. The file is in
  no M8 task's TOUCHES.
  OWNER: unassigned — needs a task whose TOUCHES can hold `UnrepresentableLocatedDataTests.cs`.

STATUS: READY
