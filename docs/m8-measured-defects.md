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
