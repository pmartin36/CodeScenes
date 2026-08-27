# Completed milestones

Milestone specs move here (from `specs/`) once **both** are true:

1. The milestone's `SceneBuilder.Core.Tests` are green in CI (real, headless behavior tests).
2. The user's Unity confirmation checklist for that milestone passes on a real edit in the Editor.

Completed so far: M0-M6 (01-07), M-Auto and its supporting milestones (14-23), M10 prefab overrides (11),
M-nested-props (24), typed prefab façades (25), the CodeScenes analyzers toolkit (26), typed child
selectors (27), typed asset catalogs (28), multi-scene builders (29), the live-verify bug-fix pass (30),
M-UI RectTransform sync (13), the hidden-serialized-field reader contract (31), and serialization
boundary fidelity C1-C4 (32).

Note: 29, 30 and 13 additionally passed live-editor validation via the Unity CLI (`unity-live-verify`), not
just the batchmode gate — 30 fixed a cluster of gate-passing defects that live testing surfaced (see its
spec).

13 (M-UI) landed gate-green at `GATE PASS: Core + Unity EditMode green (passed=447 failed=0 skipped=0)` and
was confirmed live: RectTransform round-trips both directions, argument patches are surgical, and driven
Canvas/CanvasScaler fields produce a byte-identical builder file. Live testing also surfaced three defects
that are deliberately NOT folded in here — a sync-wedging NullReferenceException on two field introductions
into an expression-bodied closure, `.Component<Canvas>()` building a World Space canvas, and
`Canvas.m_SortingOrder` never syncing scene→code. Per the lesson recorded from this milestone, a milestone
does not absorb pre-existing bugs found next door; each gets its own spec.

31 is the one entry here without a dedicated live pass: the reader must surface every field Unity
hides from the default inspector, not just what `NextVisible` draws. It landed gate-green at 458 and
recovered component enable/disable syncing plus Canvas/Rigidbody/MeshRenderer hidden state. Partial
live evidence arrived incidentally on 2026-07-31 — `Rigidbody.constraints`, one of the fields 31
recovered, round-trips correctly in a real editor — but 31's headline claim, component
enable/disable syncing scene->code, still has no live confirmation.

32 is CLOSED NARROWED and IS live-verified (2026-07-31): typed native enums apply and survive a sync
byte-identical across BOTH shapes — simple (`Canvas.renderMode`) and flags (`Rigidbody.constraints`,
including out-of-order authoring, the composite `FreezeAll` member, and zero-bit removal, with the
authored member order preserved rather than re-emitted canonically); reset-to-default removes the
setter 3/3; a code-authored Canvas builds Screen Space - Overlay with all three modes reaching code;
and ColorBlock/FontData emit compiling public-property initializers that converge with zero edits on
a second sync. It delivered its two
owners — the per-type default template (C2+C3) and the value representation contract (C1+C4), plus
`ValueWalk.cs` as the single walk every value path uses — at `passed=517 failed=0`, mutation-checked,
with every prior test surviving unweakened. But it is gate-verified only, and every one of its six
defect classes was originally found in a live editor while the gate sat green at 458 tests. Read its
"Verification status" section before trusting it. C5, C6 and the round-trip proof suite that would
close that gap moved to `specs/33`; partial, explicitly unvalidated work on them sits in `795eba2`.

**33 — boundary defects and round-trip proof.** Closed the two active defects on `main`: a
code-authored `NodeHandle.None` now clears the live reference for both a native PPtr and a
MonoBehaviour `GameObject` field (C9, `fb6d1da`), and a component referencing another component on
its own GameObject emits source that compiles instead of `CS0841` (C10, `6ae66dc`, fix-forward only
— no file carried the broken line). Its cross-cutting invariant, one located report channel with the
object anchor, component type and field as required construction data, landed in `cca7349` and is
what C5 and C7 both call rather than each formatting their own message. C5 reports the colliding
candidates instead of returning a silent null, C6 stops one Inspector control yielding two authored
fields, and C7 surfaces an unrepresentable field as a located console warning that never writes an
advisory comment into the author's builder file (`87f38f6`). C8 dissolved on measurement: the spec
claimed a value-preserving struct member reorder costs one plan op; a live EditMode probe recorded
**zero** (`FieldMap.Equals` is key-based), and `scene.isDirty` turned out to be a vacuous instrument
in batchmode, so it closed with the measurement recorded and nothing built (`a103bdf`). The
bidirectional round-trip proof suite and its live pass landed in `474c0c6` behind a frozen shared
harness whose two entry points perform drive-and-converge as a single call, so a proof cannot assert
a round trip while omitting convergence. Final gate `passed=580 failed=0`, and 22 round-trip tests
confirmed in a live editor, not batchmode alone.

It also shipped one regression its own accept criteria could not see: C6's exclusion makes an
authored `m_SortingLayer` non-convergent forever, since the excluded field is in neither the snapshot
nor the type template and the reconciler only walks snapshot fields. No builder authors it today, so
nothing is broken; it is filed as `specs/35` D2 with the approach decision stated rather than guessed.

**35 — reference writes, sorting-layer convergence, and cache invalidation.** Four defects spec 33's
build surfaced, each adversarially audited before being written down rather than taken as filed —
two of the original claims were REFUTED that way and never became work. D1: authoring a populated
`List<GameObject>` resized the live array and left every element null, because `EmitFieldOp` had no
arm for a list of `ObjectRef` and `WriteProperty` had no `ObjectRef` case and no `default:` arm, so
each element fell through the switch silently. Descent is now owned by one mechanism over
`ValueWalk`, and the four top-level-only gates route through it — two of which plan review found
after the plan had named only two, and which would otherwise have shipped a `NotSupportedException`
and a silent data-clearing patch (`7640919`). D3: the incremental snapshot cache had no invalidation
site, so the manual Sync and Build menu commands could leave it serving nodes computed under a stale
IdentityMap; it is now keyed on the map's generation at one site every assemble path inherits
(`2a634a8`). D4: sync value-patches inside the author's own typed selector, which cannot take an
`InstanceHandle`, so rewiring a field onto a prefab-instance root wrote source that would not
compile; the authoring surface now shares a `SceneObjectHandle` base (`225400d`, `6018199`). D2 was
held back for an explicit decision — excluded fields are ONE-WAY and made loud, reported through
spec 33's located channel rather than pruned from the author's source, because silently deleting a
line someone typed is its own surprise (`71482ab`, `d4931be`, `94a8b6e`).

Its own build then exposed a regression in kind: D1 gave reference lists a rendering, and a list
mixing a plain-GameObject target with a prefab-instance root emitted `new[] { door, tank }` —
`CS0826`, silently, straight to disk, where before D1 the same input had thrown loudly. Fixed by
rendering an explicit element type (`2c3dc36`). The test that should have caught it was green and
asserting a false contract: it used two tokens that were both `NodeHandle`, so "all handle tokens
stay implicitly typed" held by accident. Live-verified across all four items in a real editor —
elements assigned by `ReferenceEquals` against the live roots, zero plan ops proven three ways,
emitted source compiling on the rewire — at `passed=617 failed=0 skipped=0`.

## 36 - Uniform value descent

`ValueWalk` documented itself as THE recursion over a value's container structure, but five passes
did not use it: `AssetRefLowering` and `BuiltinRefValidator` each hand-rolled the same
`switch (node) { AssetRef / List / Nested / default }`, `PlanningValidator.WalkAssetValue` had a
third copy, `SerializedFieldBridge.WriteProperty` inlined its own, and `SourceExpr.ValueNodeLiteral`
could not route through anything because it returns a `string` and `Map` is node-in/node-out.
Nothing was broken, because no feature had added a container kind since those accumulated. M8 adds
one, so every one of those passes would have walked straight past a UnityEvent listener's
references: an authored asset target would have kept an empty `Guid` forever, re-syncing on every
pass and then being skipped at execution.

All five now route through `ValueWalk`, which gained `Fold<T>` (for renderers) and
`Descend<TContext>` (parent-before-children with the list index, which `WriteProperty` needs to
reach `GetArrayElementAtIndex`). `Fold` takes one delegate per container kind deliberately: adding a
kind changes the signature, so every call site fails to compile until its author says how the new
kind renders.

Removing the `default:` arms and letting the compiler find the gaps was measured and rejected: C#
reports `CS8509` on a switch over an `abstract record` with sealed cases even when every case is
covered, so a discard arm is mandatory. Enforcement is a Roslyn token scan instead
(`ValueContainerDescentScanTests`), matching `ValueNode.List`/`ValueNode.Nested` in any position
rather than textual prefixes, because most descent here is written as switch-expression arms that
`case`/`is` matching misses entirely.

Behavior preservation was proven structurally: every characterization test committed in a bucket
whose diff touches zero production files, so its green gate is after-the-fact `git`-checkable proof
those tests passed against unmodified code, plus a content-hash pin over the pre-existing tests of
all five passes, retired once the migrations landed. A second permanent guard
(`GateTestMetaFileTests`) asserts every `unity-gate` test file has its sibling `.meta`, because a
missing one means Unity never imports the test, so it never runs and the suite is green for the
wrong reason. Gate `passed=637 failed=0 skipped=0`. Live-verified in a real editor: materials
(an `AssetRef` nested in a `List`), builtin meshes and `Vector3` nested values all resolved on
Materialize, a second build applied zero plan ops, and an Inspector edit of `m_Size` synced back as
`new UnityEngine.Vector3(7f, 8f, 9f)` through the new `Fold`-based renderer.

Still pending in `specs/`: 10 (M9
SerializeReference) and 34 (licensing, blocked on
two spikes). `00-foundation.md` stays in `specs/` as the living base contract. Unowned defects
measured during builds, with no task claiming a fix, are tracked in `docs/open-defects.md`.

## 08 - M7 robustness (rescoped)

The bidirectional loop's hardening pass. Two of the spec's five deliverables were already shipped
under M-Auto (self-triggered event suppression via `SuppressionScope`, and domain-reload
re-subscribe via `[InitializeOnLoad]`), so this build delivered the genuinely-remaining three:
persisted resync state, external-edit recovery on reload/focus, and a determinism/no-churn audit.

Core (`d5f621d`) added `SyncCheckpoint` — a sibling `FooScene.sbstate.json` holding the canonical
`LastSnapshotHash`/`LastSourceHash`/`LastSidecarHash` — and `CheckpointRouter`, which decides sync
direction on reload from those hashes: only-scene-changed => scene->code Reconcile, only-source
=> code->scene Materialize, both-changed => conflict surfaced (never last-write-wins). Hashing goes
through the existing canonical serializer (`CanonicalHash` over `SceneModelSerializer`/
`IdentityMapJson`), not in-memory `GetHashCode`. A second Core bucket (`00b522e`) added the
determinism, round-trip-idempotence and no-id-churn audits as headless tests. The adapter
(`87c8ba3`) added `SceneBuilderResync` (the reload + focus-regain full-snapshot resync, authority
is a fresh snapshot per §5, not the event stream) and `SyncCheckpointWriter`, persisting state to
disk so nothing sync-critical survives only in static fields a reload would clear.

Gate `passed=679 failed=0 skipped=0` (`GATE_FORCE_UNITY=1`). Live-verified in a real editor
(`unity-live-verify`, log `SceneBuilderTest/Logs/live-verify-m7-1.log`) on an isolated `M7Fixture`
to avoid the demo scene's known FitSize/SurfaceSnap drift: self-event suppression produced no echo
re-patch, `sbstate.json` wrote all three hashes and was byte-stable on a no-op rebuild, a
disk/`sceneOpened` edit resynced source with no ObjectChangeEvent, a forced domain reload re-armed
subscriptions and resynced from disk, a 10x round-trip kept every GlobalObjectId/LogicalId
byte-identical, and two builds produced identical sidecar+sbstate. The one slice not observable
headless is OS focus-event *delivery*; the resync/routing logic it triggers was exercised via the
equivalent `sceneOpened` hook and a direct call to the focus target `ResyncActiveScene()`.

## 09 - M8 UnityEvents / OnClick wiring

Author and round-trip UnityEvent persistent listeners (Button.OnClick and the generic
`.OnEvent(x => x.field, ...)`) in both directions: target (scene component or asset), invoked method,
call state, a single typed static argument (int/float/string/bool/object), and the dynamic
(EventDefined) form that forwards the event's own runtime args. The method reference is a typed
method-lambda (`x => x.Method(args)`), so the shipped SB1201/SB1202 analyzer and the compiler check the
signature; a listener target is a component `LogicalId`, not a GameObject one.

Built over several passes. The model layer, the component-target classifier, the projection (measured
mode/call-state numbers), the serialized-path vocabulary + its scan guard, and the full
`ComponentRef<T>`/`Ref<T>`/`OnClick`/`OnEvent` authoring surface shipped earlier. The final run
(after the plan-engine bucket `f040769`) delivered the adapter I/O (`cf3bb2d`:
`UnityEventReader`/`UnityEventWriter` over Core's projection, SerializedObject on
`m_PersistentCalls.m_Calls`), the Core reconcile (`08ae86c`: component-target resolution made the
reconciler treat a component LogicalId as resolvable, plus the listener source-patch with an
`InsertListenerCall` edit so an added listener never overwrites an existing one), and the bidirectional
round-trip proof (`836e7c3`). The reconcile work was split into an invariant-owner task and per-delta
render tasks after an earlier single oversized task escalated; the required RED matrix (add-preserves-
order, target-change in place, remove, multi-add, `.OnEvent`, dynamic) is pinned so a symmetric
operation set can't ship a subset.

Gate `passed=713 failed=0 skipped=0` (`GATE_FORCE_UNITY=1`). Live-verified in a real editor
(`unity-live-verify`, log `SceneBuilderTest/Logs/live-verify-m8-1.log`): all eight confirmation-checklist
items passed on throwaway fixtures. OnClick wires target/method/RuntimeOnly; retarget, int arg (7),
object-mode asset target (sidecar `assets[]` gains the GUID), and EditorAndRuntime call state each
round-trip to the exact source line; removing an entry deletes its statement; a dynamic
`Slider.onValueChanged -> Hud.SetValue` authored form round-trips; a second Materialize on a converged
Button+OnClick scene emits zero plan ops.

Live testing also surfaced ONE defect that is deliberately NOT folded in here, per the rule that a
milestone does not absorb a pre-existing bug found next door: a plain `UnityEngine.UI.Slider` authored
via generic `Component<Slider>` drifts scene->code, emitting 5 phantom RectTransform PatchArgument edits
on every sync and never reaching a fixed point (and, downstream, masking a fresh listener add on a
Slider). It predates M8 (no listener path touches RectTransform), is spec 13 / RectTransform territory,
and is filed in `docs/open-defects.md`. M8's item-7 round-trip proof was scoped to the listener contract
for that reason; item-8 proves full no-op idempotence on a Button (no RectTransform).

## 37 - RequireComponent(RectTransform) hosts must not drift a phantom .RectTransform()

A spec-13 follow-on defect surfaced by M8's live-verify. A node authored via generic
`.Component<Slider>()` (or any `[RequireComponent(typeof(RectTransform))]` host) with no explicit
`.RectTransform(...)` stayed model-side `Kind="Transform"` while its live transform was a
`RectTransform`, so the differ's promotion arm injected an unauthored `.RectTransform(defaults)` on the
first sync over a converged build, breaking the first-sync-no-op invariant. It was not value drift and
converged on the second sync; the injected call was the whole problem.

Fix removes the Kind mismatch at its source: an adapter predicate answers whether a component `TypeRef`
carries `[RequireComponent(typeof(RectTransform))]` (reflection, transitive), applied as a stage at
`DesiredModelLoader.Load` (the one seam both directions load the desired model through), so a
require-RectTransform host carries `Kind="RectTransform"` and the differ takes the omit-at-default
matched arm. The spec-13 D6 promotion arm is untouched; it simply stops firing for these hosts.
Gate `passed=720`. Live-verified: `.Component<Slider>()` build then sync is `PatchEdits==0` with no
`.RectTransform` injected, and an in-editor `anchoredPosition` edit emits exactly
`.RectTransform(anchoredPos: (-40f, -30f))` (only the changed channel), second sync a no-op. M8's Item7
round-trip proof was re-tightened to the strict fixed-point assertion as part of this.

## 38 - the adapter MemberSpellings producer

A real M8 scene->code gap, caught only because M8's live-verify exercised a fresh in-editor listener
add: a persistent UnityEvent listener wired in the Inspector on any serialized UnityEvent field EXCEPT
`Button.m_OnClick` was silently dropped (`UnsyncableListener`, "no public C# spelling") instead of
reconciling to `.OnEvent(x => x.field, ...)`. The decisive variable was the field key. Root cause: the
adapter snapshot factory `SceneSnapshotReader.FromRoots` never populated `SceneSnapshot.MemberSpellings`
(no producer existed anywhere in the adapter; the sibling `ComponentDefaults` was populated right beside
it), so the reconciler could not spell a non-`m_OnClick` event field. A textbook mocked-test blind spot:
the Core `.OnEvent` tests hand-fed `MemberSpelling[]` into the snapshot and passed green, and one test
even pinned the drop as correct, while the live adapter fed nothing.

Fix supplies the missing producer: one `MemberSpelling` per UnityEvent field at `FromRoots`, deriving
the public name through the existing `SerializedMemberMap.TryPublicMemberName`, covering the cold and
incremental (auto-sync) read paths. No Core change. The primary RED test is EditMode by necessity, since
a Core test structurally cannot see an adapter-production gap; the mis-pinning Core test was corrected to
assert the drop only for a genuinely unspellable member. Gate `passed=720`. Live-verified: a fresh
in-editor listener on a plain `UnityEvent onPlain` field syncs to
`.OnEvent(x => x.onPlain, target, t => t.Foo())` with zero drop-notes, a dynamic `UnityEvent<float>`
field syncs with `dynamic: true`, and `Button.m_OnClick` still round-trips.

## 10 - M9 SerializeReference polymorphism

Author and round-trip `[SerializeReference]` managed-reference fields (interface/abstract/base-typed
fields holding a concrete instance) in both directions: null, concrete-type change, the instance's own
serialized fields, and nested managed references at arbitrary depth. Authoring is
`SetRef(x => x.field, new Concrete { ... })` (and `null`); the `new T { ... }` object-initializer
round-trip was already shipped for `ValueNode.Nested`, so M9 reused it rather than rebuilding it (an
object-initializer spike established this before the run and reshaped the decomposition around the real
risks).

The model layer added `ValueNode.ManagedReference(TypeRef? concreteType, FieldMap fields)` as a new
`ValueWalk` container kind (all five walk primitives plus the descent-scan guard, the
`UnityEventListeners`-sized job), the `SetManagedReference` plan op, and the `Differ` whole-node arm (a
type change replaces the instance, never a field-level patch). Parse added a `SetRef`-only
`ParseManagedReference` and the `.SetRef` recognizer allowlisting in both `FlatShapeRecognizer` and its
analyzer twin. The genuine-depth work was object/asset refs INSIDE a managed instance's fields:
`NestedValueEmission` excluded those kinds, so resolution and handle pre-rendering had to descend into
`ManagedReference.fields`. Built in two buckets (`e453cb9` Core, `5b7d9b5` adapter).

Gate `passed=744 failed=0 skipped=0` (`GATE_FORCE_UNITY=1`). Live-verified across all eight
confirmation items (`SceneBuilderTest/Logs/live-verify-m9-1.log`): a `new Aggressive { range = 5f }`
materializes to a live `Aggressive` managed reference; an in-scene `range` edit round-trips to `9f`
with the type unchanged; switching to `Flee` rewrites the whole construction with no stale
`Aggressive` fields; None syncs to `SetRef(..., null)`; a nested `Composite` round-trips a nested
`primary.range` edit; a converged scene is idempotent both directions; an object ref inside the
instance (`new Aggressive { target = doorA }`) resolves live and round-trips to `doorB` on reassign;
and a manufactured missing type surfaces a located conflict with source untouched and the field not
nulled. Two low follow-ups are filed in `docs/open-defects.md`, both non-blocking: a list of managed
refs needs a shared element type token, and a null object-ref member inside a managed instance renders
explicitly (`target = NodeHandle.None.As<...>()`) rather than being omitted at default. The latter is a
verified fixed point (cosmetic verbosity, not churn).

## 39 - Flat prefab authoring

Define and save a reusable prefab ASSET from a code builder (the inverse of M6, which only instances an
existing prefab): one builder `.cs` per prefab under a `Prefabs/` folder, round-tripped and seamless.
Scope is FLAT (single-root hierarchy of GameObjects/components/transforms/asset-refs); nested prefabs and
variant chains are deferred to `specs/needs_research/nested-prefabs-and-variants.md`.

The centerpiece is a shared build/sync core, not a fork: the milestone refactored the scene
`SceneBuilderBuild.Run`/`SceneBuilderSync.Run` to a single build/sync TARGET seam and added a prefab
target as a second implementation, so scene and prefab share the whole parse -> materialize -> diff ->
reconcile -> source-patch -> sidecar pipeline and differ only in a handful of editor-boundary hooks (read
roots, execute-into-target, persist, identity-stamp, edit-trigger, routing). The load-bearing invariant,
grounded in two spikes: regeneration edits the LOADED CONTENTS in place (`LoadPrefabContents` -> mutate
-> `SaveAsPrefabAsset` -> `UnloadPrefabContents`), never overwrite-from-fresh, or Unity re-mints the
fileIDs of unchanged objects and breaks edit reconciliation. Identity anchors on `GlobalObjectId` (which
resolves off-scene for asset internals); editor prefab edits ride the existing `ObjectChangeEvents`
channel and code-side `.cs` edits ride the existing file-watcher, both under the one persisted master
auto toggle.

Built in two buckets (`d0e2a1e` shared seam + code->prefab, `278aa4b` sync-back + routing +
self-registration + seamless auto-sync). Gate `passed=773 failed=0 skipped=0`. Live-verified across all
seven confirmation checks: a code builder produces a real `.prefab` with the authored hierarchy; a
Prefab-Mode field edit auto-syncs back to the `.cs`; a `.cs` edit that changes a field AND adds a child
regenerates edit-in-place with the unchanged children's fileIDs byte-identical (the make-or-break
proof); a second build is idempotent; the code-defined prefab instances as a Connected instance and
auto-registers into the `Prefabs.X` facade; seamless auto-sync fires both directions with no button; and
scene build/sync still work through the refactored shared seam (non-regression). One prose-vs-code note:
the shipped identity stamp uses `GlobalObjectId idType=2` (the object is stamped while in the
prefab-contents preview scene), not the `idType=1` the spec text stated; it is fully functional and
round-trips.

## 40 - Nested prefabs and variants

The depth layer on flat prefab authoring (spec 39): author, from code, NESTED prefabs (a code-defined
prefab that instances another prefab inside itself) and prefab VARIANT chains (a code-defined prefab
whose base is another prefab, carrying an override layer), round-tripped and seamless. Nesting via a
typed `NodeHandle.Instance<TRef>` verb on `PrefabRoot`; variants via a new `IPrefabVariantDefinition` /
`VariantRoot` that names a base and carries override-only authoring. A variant's base may be ANY real
`.prefab`, human-made or code-defined (measured: no API difference). v1 override depth is
existing-vocabulary only (the base's or nested instance's own hierarchy); deep layer-qualified
addressing is deferred.

Built entirely on reuse: the shared build/sync target seam from spec 39 (no fork) and the
provenance-agnostic M10 + spec-24 override machinery (base-vs-override disentanglement is inherited, not
rebuilt). Two spike-measured invariants are owned with checks: the nested child must be a connected
instance at save time (instantiate-then-parent, never build inline) or the nesting flattens, and
nested-instance identity is read from persisted state. Nested materialize runs `InstantiatePrefab` into
the prefab-contents preview scene; variant materialize is `InstantiatePrefab(base)` ->
`RecordPrefabInstancePropertyModifications` -> `SaveAsPrefabAsset(Variant)`. Built in two buckets
(`2b40b5d` nested, `ed219b0` variant).

Gate `passed=795 failed=0 skipped=0`. Live-verified across all confirmation checks
(`SceneBuilderTest/Logs/live-verify-spec40-1.log`): a code builder produces a prefab holding a genuine
connected nested instance; a nested override authored at existing-vocab depth round-trips (Prefab-Mode
edit auto-syncs back); an edit-in-place regeneration that adds a child keeps the nested instance's
fileID byte-identical (the make-or-break proof); a variant of a code base AND of a human base each
produce a real `Variant` asset whose override persists and whose base changes inherit through; a variant
edit auto-syncs back; both directions fire automatically under the master toggle; and flat-prefab plus
scene build/sync still work through the shared seam (non-regression, scene build idempotent).

## 41 - Snapshot-emit classification

Three reproduced defects sharing one root cause: a snapshot value that reaches source emission without
routing through the shared classification (`SnapshotFieldEmission`/`ClassifySnapshotRef`) is emitted
wrong. A bare `ValueNode.Unsupported` landed as a raw token at three sites (CS0103); `BuildOverrideSetSpec`
rendered a dangling `ObjectRef` as a phantom identifier (CS0103); an added-child component `ObjectRef`
field threw `NotSupportedException` and crashed `SourcePatchApplier.Apply`. All three were reproduced by
an out-of-tree probe over the public `Reconciler.Reconcile`/`SourcePatchApplier.Apply` before the spec
was written (repro-first: the RED shapes seeded the pipeline, and a fourth candidate defect from the same
register was refuted this way and re-scoped instead).

Fix (pure Core, built in one bucket, `d5f5cb6`): a bare `Unsupported` classifies as unemittable and every
snapshot-emit site (`BuildOverrideSetSpec` included) routes through the one classification and omits-and-
reports rather than writing a raw token or phantom identifier; and a new `FieldExpressions` channel on
`AppendInstanceAddChild`, threaded from `ReconcileAddedGameObjects`, renders an added-child reference field
as a real handle instead of crashing. Gate `passed=796 failed=0 skipped=0` (`GATE_FORCE_UNITY=1`). Verified
by the gate's EditMode layer, which now runs `AddedChildReferenceRoundTripTests` exercising the live
added-child reference round-trip, plus the headless `SnapshotEmitClassificationTests`/
`AddedChildFieldExpressionsTests`. No separate live-verify session: the fix is Core-only with no new
watcher/trigger behavior, so the EditMode gate test is the live-editor check.

## 42 - Auto-sync incremental identity

Two reproduced performance defects with one root cause: auto-sync's O(changed) identity guarantee was
O(scene) per keystroke. Every debounced cycle cold-reassembled the whole scene at its tail
(`SceneBuilderAutoSync.CaptureBaseline` -> `AssembleCold`, discarding the incremental cache), and the
scene-reference resolver resolved each target's `GlobalObjectId` uncached, bypassing the counted
`GlobalObjectIdCache` seam the perf gate asserts on.

Fix (adapter, one bucket `0c4feff`): one counted identity seam threaded to node identity, reference fields
and listener targets alike (Task A), and a baseline-reuse lifecycle so a converged cycle reuses its
incremental snapshot instead of cold-reassembling, cold-reading only on a generation bump or cold session
(Task B). The perf gate `AutoIdentityTests` was extended to N reference-holding objects with a target-move
byte-equality proof of the invalidation policy, so neither defect can return without failing it. Gate
`passed=803 failed=0 skipped=0` (`GATE_FORCE_UNITY=1`). Live-verified in a real editor
(`Logs/live-verify-spec42.log`): idle pump ticks hold `GlobalObjectIdCache.ResolutionCount` at 0 (no cold
reassemble), a value edit costs a bounded non-accumulating delta, and scene references round-trip through
the cache-threaded resolver with no dangling reports.

## 44 - Instance-override build convergence and reporting

Two reproduced defects, both the code->scene BUILD path silently declining a prefab-instance override that
the sync path would surface. An authored override on an adapter-excluded serialized path
(`SpriteRenderer.m_Size`, `m_SortingLayer`) emitted a `SetInstanceOverride` op on every build forever with
no report, because `ExcludedFieldGate` (spec 35 D2) reached `Differ.EmitComponentEdits` but not
`InstanceOverrideDiff.Emit`. And a stale override detected on the build was suppressed with a
null-`RecurrenceKey` conflict that `SurfaceNotes` drops, while the sync path surfaced the same decline.

Fix (adapter + Core, one bucket `da380f0`): `ExcludedFieldGate` threaded into every instance-override emit
surface (`SetInstanceOverride`/`AddInstanceComponent`/`AddInstanceChild`), with `ExcludedFieldAudit`
extended to attribute `SetInstanceOverride` (Task A, owns the bypass check); and a stable per-target
recurrence key on `StaleOverride` and the excluded-override report, routed both directions through the
keyed once-per-session `SurfaceNotes` channel at WARNING severity (Task B). Gate
`passed=807 failed=0 skipped=0` (`GATE_FORCE_UNITY=1`). The gate's EditMode layer is the live-editor check
here (as with spec 41): the new behavior is deterministic build-path diff emission plus console reporting,
with no new watcher/pump/reload behavior that batchmode is blind to, and the shipped EditMode tests drive
real prefab instances in a live scene - asserting an excluded-path override converges (second build zero
ops) with a located WARNING surfaced once naming instance + component type + path, and the stale override
surfaced on the build and not re-logged in-session.

## 43 - Reference forward-declaration (declare-before-use)

A scene reference from an earlier-declared object to a later-declared one emitted a folded reference above
the target's declaration, so the generated builder failed to compile (`CS0841`). Reproduced live over two
manual syncs.

Fix (Core, reconcile-internal, no authoring-API surface): declare-before-use. The reconciler emits the
reference target before the reference-bearing component, so the reference folds in the ordinary
`.Component<T>(c => c.Set(...))` closure and compiles, the same shape a human or an LLM would write. It
converges because a node's components and children are independently-ordered peer lists
(`StatementPlacement.cs:35-37`) and the component reorder pass keys on components only
(`ComponentReconciler.cs:200-232`), so placing the target ahead of the component never churns. Two paths:
the from-scratch emit order in `ReconcilerAppends` (child before the referencing component, child gets its
own handle, which also closed a `CS0103`/dropped cross-ref defect found during verification), and the
two-sync introduce path routed through the generalized placement floor instead of the in-place fold.
Commits `3344530` (Core) + `e548dc6` (EditMode). Gate `passed=821 failed=0 skipped=0` (`GATE_FORCE_UNITY=1`).

Chosen after rejecting relocation (churns the component reorder pass), a capturable `ComponentHandle<T>`
return (breaks fluent chaining, drags in Runtime/parser/DocGen), and a `Configure<T>(ordinal)` verb (a
codegen-only construct an LLM would not author from scratch, plus a fragile source ordinal). The EditMode
layer is the live-editor check: a 15-scenario matrix (9 forward-reference cases plus 6 regression cases,
including a pre-existing converged multi-node scene re-syncing to zero edits) drives real editor scenes
through `BuilderCompileCheck`, each asserting compile and a fixed point.

## 34 - M-Licensing (activation, seats, the 14-day trial)

Paid-tool licensing for the Gumroad build: in-editor key activation, 3-machine seat management, a
14-day trial, and a once-per-day async entitlement check that never blocks domain reload or sync. A
valid signed token is validated offline, so a licensed user in steady state sees nothing. Unlicensed
freezes auto-sync in both directions and disables the Build/Sync menus without ever leaving a sync
half-applied; the `LicenseGate` (main assembly, default-allowed) is the one seam every sync entry
point consults, and the licensing assembly (`SceneBuilder.Licensing.Editor`, separately excludable
for the Asset Store build) registers the real `LicenseState`-backed verdict.

Two spike results overturned the spec's assumptions, both measured, not guessed: **the token is
RSA-2048 / PKCS#1 v1.5 / SHA-256, not ECDSA P-256** (`ECDsa` implements neither `ImportParameters`
nor `ImportSubjectPublicKeyInfo` on Unity 6000.5.3f1's Mono profile; RSA verifies via
`RSACryptoServiceProvider`), and the machine identifier is `SystemInfo.deviceUniqueIdentifier`
(byte-identical to `/etc/machine-id` on Linux; Windows/macOS legs still to be measured). The backend
half (activate/trial/seats/release functions, Gumroad verify behind an injectable seam, RSA signing)
lives in `CodeScenesSite` and is deployed at `us-central1-codescenes.cloudfunctions.net/license`.

Built through the tdd-pipeline (commits `1e64523`, `2e8d691`, `9f7e971`, `4756951`), then a UI/menu
polish pass (`0af98e7`): the activation window is sectioned inspector-style, the menu is Auto-sync /
Build ▸ / Sync ▸ / — / License with Build and Sync greyed while Auto-sync is on, the Activate button
animates an "Activating" state, malformed keys are rejected client-side, and removing the current
machine's own seat clears the local token. Gate `passed=921 failed=0 skipped=0` (`GATE_FORCE_UNITY=1`).

Live-verified twice via `unity-live-verify` (`SceneBuilderTest/Logs/live-verify-licensing-1.log`):
real-backend activation licenses the machine and arms sync, an unlicensed transition freezes it and
disables the menus with a clean console, and re-activation resumes with no seat leak. The full Gumroad
happy path was also confirmed by hand end to end (a real test-purchase key -> deployed `activate` ->
seat bound in prod Firestore -> issued token verifies against the editor's embedded public key).

Known follow-ups (not blockers): the Windows/macOS legs of the RSA and machine-id spikes, an optional
"Deactivate this machine" button on the Licensed view, and a Cloud budget alert on the backend.

## 45 - Between placement + frame-aware spatial kernel

New editor-time `Between` component: `node.Between(from:, to:, fraction:, axis:, alongOrientationOf:)`
places an object at `fraction` (0..1, unclamped) along one named axis between two anchors, flush-bumping
at 0 and 1, moving only along that axis so the perpendicular offset between anchors (e.g. a height
difference) is ignored. Axes are world by default; `alongOrientationOf` supplies a rotated reference for
tilted content. It is hierarchy-independent (anchors contribute world position/bounds only) and
back-solves `fraction` when the object is dragged along the axis. The world-AABB face math is replaced by
a shared `ProjectedExtent` kernel that `SurfaceSnap` and `FitSize` now use too (a regression test asserts
untilted scenes are byte-identical). A second position-driver now exists, so `PositionAuthority` adds
per-axis conflict handling: a compile-time analyzer diagnostic plus deterministic runtime arbitration
that yields the lower-priority driver with one warning. `RotationX/Y/Z` channels are reserved (not used)
for a future `OrientToSurface`; align-to-normal is explicitly out of scope here.

Built through the tdd-pipeline (commits `0d9ef00`, `54e68bc`, `833e46f`). Gate
`passed=947 failed=0 skipped=0` (`GATE_FORCE_UNITY=1`).

Live-verified via `unity-live-verify` (`SceneBuilderTest/Logs/live-verify-between-1.log`): all six
scenarios observed in a running editor against real `transform.position`/bounds — flush at
`fraction` 0/0.5/1, overshoot past both anchors at -0.25/1.25, Y untouched when anchors differ in height,
placement following a 45deg-yawed root under `alongOrientationOf`, and the same-axis `SurfaceSnap`+`Between`
conflict emitting exactly one warning while Between defers. The key case: dragging a placed object to
`(7,3,2)` back-solved `fraction` 0.5 -> 0.75 from the along-axis component while the perpendicular Y/Z
stayed exactly as dragged, idempotent on re-eval. The runtime-field back-solve was observed live; the
source-file leg of sync-back is covered by the `RoundTripBetweenSync` EditMode suite. Console clean of
product errors.

## 46 - sync-back emits compiling C# (private members, forward declarations)

Two scene->code emitter defects that each wrote non-compiling builder source (and led the first
minigolf one-shot to disable two-way sync) are fixed. A non-public serialized field
(`[SerializeField] private` / internal) now emits the raw-string `c.Set("<serializedName>", value)`
form instead of a typed selector `c.Set(r => r.field, value)` that cannot compile against a private
member; the emitter carries the member's accessibility via `SceneSnapshot.InaccessibleMembers` +
`MemberSpelling` so the choice is one shared decision, not per call site. Spec 43's declare-before-use
ordering was extended in `StatementPlacement` to the recurring case a handle referenced by an
earlier-emitted statement, with a guard that fails when any emitted statement names a `var` declared
on a later line. Covered by Core tests (`DeclareBeforeUseGuardTests`, `NonPublicFieldSelectorEmitTests`)
and EditMode gate tests (`NonPublicFieldSyncCompileTests`, `SyncBackCompilesRoundTripTests`).

Built through the tdd-pipeline (commit `7cb6411`). Gate
`GATE PASS: Core + Unity EditMode green (passed=949 failed=0 skipped=0)` (`GATE_FORCE_UNITY=1`).

Live-verified via `unity-live-verify` (`SceneBuilderTest/Logs/live-verify-spec46.log`): against a
running editor, (a) a `[SerializeField] private float` set in the scene synced back as
`c.Set("hiddenValue", 42f)` and the product's own `BuilderCompileCheck` returned zero errors; (b) a
forward reference hoisted `var beta` above its use with zero `CS0841`; (c) the builder was byte-identical
across three consecutive syncs. Live-verify also root-caused a convergence defect the (b) hoist itself
triggers: when a referrer sits at a lower sibling index than the handle it references (the natural
add-A-then-B case), the hoisted `var` declaration order fights the sibling-order reorder pass, which
emits two byte-identical `ReorderStatement` no-ops every sync — suppressed by the pre-existing 322e143
guard (so the file stays byte-stable) but logged as an Error each cycle. Filed in `docs/open-defects.md`
not folded into 46's commit; 46's own `SyncBackCompilesRoundTripTests` dodged the shape via
`door.SetSiblingIndex(2)`. Fixed RED-first as a follow-up: the reorder pass in
`SceneBuilder.Core/Reconcile/Reconciler.cs` now consults a `ForwardReferencePinnedReorders` guard
(`Reconciler.Reorder.cs`) that pins both ends of any inline forward reference (a chained
`Add(...).Component<T>(c => c.Set(field, sibling))` naming a sibling the scene orders after the owner)
so the unachievable reorder is never emitted; the hoist-forced order is the converged target.
`ForwardReferenceConvergenceTests` (Core) asserts a true fixed point (`PatchEdits == 0`, not
suppression) and a non-dodged case was added to `SyncBackCompilesRoundTripTests` (EditMode). Gate
`GATE PASS: Core + Unity EditMode green (passed=959 failed=0 skipped=0)`.

## 47 - typed member-selector resolves the component's REAL serialized names

`AuthoredPathResolver.ResolvePath` no longer guesses a serialized path from a fixed
`field -> m_Field` camelCase->PascalCase mangling; it enumerates the live `SerializedObject` probe it
already holds and matches the authored member against the component's ACTUAL property names (exact
propertyPath, then the reflected `[SerializeField]`/`[FormerlySerializedAs]` spelling, then a
case-insensitive match against the probe's real names and their de-`m_`/de-space normalization). Enum
members carry through the existing enum->int lowering, so an enum-typed typed selector writes without a
raw `(int)` cast. A member that maps to no real property still throws the located
`Cannot resolve authored member ...`, and a member Unity splits across two fields (TMP `alignment`)
fails located and names both real fields. The invariant: ResolvePath returns only a path the probe
reports, so a fabricated path is unreachable by construction.

Built through the tdd-pipeline (commit `7c82840`). Gate
`GATE PASS: Core + Unity EditMode green (passed=958 failed=0 skipped=0)` (`GATE_FORCE_UNITY=1`).

Live-verified via `unity-live-verify` (`SceneBuilderTest/Logs/live-verify-spec47.log`): each member
authored through the typed selector `.Set(x => x.<member>, value)` and built code->scene into a fresh
scene, then read back from live component state. `Camera.nearClipPlane`/`fieldOfView` resolved to the
spaced `near clip plane`/`field of view`; `Light.type`, `Image.fillMethod`,
`Rigidbody.collisionDetectionMode` (`m_CollisionDetection`) and `Rigidbody.interpolation`
(`m_Interpolate`) resolved and wrote their divergent real serialized fields as bare enum literals with
no `(int)` cast, `diagnostics=0` per build. The no-such-member and TMP `alignment`-split cases threw the
expected located errors (alignment naming both `m_HorizontalAlignment`/`m_VerticalAlignment`).
`TextMeshProUGUI.fontSize` (`m_fontSize`) is covered by the green EditMode gate test and was
additionally captured live. Console errors during the pass were pre-existing (a DemoScene
`m_TaaSettings` standing note on scene-load reconcile, scene->code and out of 47's scope) or engine
environment noise, none caused by 47.

## 48 - a [RequireComponent]-required component is preserved, not churned

The `Differ.EmitComponentEdits` removal loop no longer emits a `RemoveComponent` for a live component
required (directly or transitively) via `[RequireComponent]` by a SURVIVING (desired) component on the
same object. `RequireComponentPredicate` was generalized to return a type's transitive required-set
(`RequiredTypeNames`), and the Editor injects it into Core's removal guard as a closure (the same
pattern `RectTransformPromotion` uses). Canonically `CanvasRenderer`, forced by every `Image`/`TMP`
`Graphic`: the builder never names it, yet it is treated as intrinsic and not removed, so no
`.Component<CanvasRenderer>()` workaround is needed. Keying on surviving requirers keeps it correct
when the requirer itself is deleted (the required component then becomes legitimately removable).

Built through the tdd-pipeline (commit `ef53405`). Gate
`GATE PASS: Core + Unity EditMode green (passed=960 failed=0 skipped=0)` (`GATE_FORCE_UNITY=1`).

Live-verified via `unity-live-verify` (`SceneBuilderTest/Logs/live-verify-spec48.log`): an `Image`-only
builder (import-free `Graphic`) never naming `CanvasRenderer` built twice code->scene; across both
builds zero `Can't remove CanvasRenderer ... depends on it` errors, the live `CanvasRenderer` present
after each (`comps=[RectTransform,CanvasRenderer,Image]`), and the second build a fixed point (0 plan
ops). Note the fix is deliberately the REMOVAL side only: scene->code still emits the required
`CanvasRenderer` as an authored `.Component<>()`, which is correct — it is a real component in the
scene and is reflected like every other. 48's aspirational "no builder ever declares a RequireComponent
dependency" was over-stated; the pain it fixed was the removal churn (Unity refusing to delete the
required component every build), not the presence of the component in emitted code.

## 49 - a code->scene build error is a discoverable state, not a lost log line

A per-builder build-status recorder `SceneBuilderBuildStatus` (`com.codescenes/Editor/`) is placed at
the shared build core `SceneBuilderBuild.RunCore`, so every code->scene and code->prefab channel
inherits it by construction: `RunCore`'s refusal/success returns and a now-caught `ParseException`
record via `RecordRefused`/`RecordClean`, and `Run`/`PrefabBuildSyncTarget.Build` (plus its two
pre-`RunCore` refusals) funnel through the same recorder. This closes the previously-silent
`SceneBuilderResync.RunMaterialize` resync channel (focus-regain / scene-open / domain-reload) that
discarded the `BuildResult`. State persists in `SessionState` (survives domain reload, dies with the
session) and surfaces two ways: a standing `CodeScenes/` menu item (`Build error: <File>:<Line>` vs
`No build errors`, pinging the offending line) and the scene-view overlay via `ConflictSurfacing`, which
gained a per-key `RemoveOverlay` so `RecordClean` drops only that builder's entry. Whole-scene refusal
is unchanged (scene left untouched). The decomposition first halted on plan validation (the recorder
was under-scoped to three call sites, blind to `RunMaterialize`); the spec was corrected to the shared
outcome point with a behavioral bypass check before the relaunch.

Built through the tdd-pipeline (commits `f968482`, `19d411f`). Gate
`GATE PASS: Core + Unity EditMode green (passed=975 failed=0 skipped=0)` (`GATE_FORCE_UNITY=1`).

Live-verified via `unity-live-verify` (`SceneBuilderTest/Logs/live-verify-spec49.log`), 5/5: an
unsupported `.On(...)` at line 7 refused with a single located `SB1000 ...(7,16)` error and the scene
untouched; the `CodeScenes/` menu read `Build error: LiveVerify49Scene.cs:7` (with the overlay
registered) while auto stayed ON, a clean builder read `No build errors`; the refusal survived a real
`RequestScriptReload` purely from `SessionState`; fixing the line built clean and cleared the state
(an unrelated conflict overlay survived, proving per-key removal); and a refusal driven through
`SceneBuilderResync.ResyncRoute` (Materialize direction) set the standing status (`SB2201 ...(6,9)`),
the exact regression the fix closes. Console clean of unexpected errors.

## 50 - auto-sync reliability (scene-open race, atomic-write blindness, pump death)

Three transport-layer defects in the auto-sync loop (`com.codescenes/Editor/SceneBuilderAutoSync.cs`),
each of which silently dropped a sync that should have run:
1. A code->scene cycle whose target scene is not yet open now DEFERS instead of dropping: on a
   `TryGetOpenScene` miss the path is re-enqueued and the deadline re-armed (`DeferCodeToScene`), so the
   next pump tick after the scene opens runs the build; a retry ceiling (10) abandons a genuinely
   never-opening builder rather than spinning forever.
2. The source watcher no longer uses a server-side `"*.cs"` name filter (which missed atomic-replace
   writes whose temp name did not match); it watches the directory unfiltered and filters by `.cs`
   extension on the main thread, so a rename-into-place is seen. A content-hash rescan of discovered
   builders on focus-regain is the backstop for any write that still slips.
3. The update pump is live after every play-mode / probe round trip with no manual domain reload: the
   `EnteredEditMode` re-arm defers via `delayCall` (so `isPlayingOrWillChangePlaymode` reads false), plus
   a self-healing `PumpWatchdog` on a class-lifetime hook re-arms whenever the toggle is on, the license
   allows, and play mode is off but the pump is not subscribed.

This also retires the stale open-defect (the old per-cycle "scene is not open — build skipped" logged at
Error): the skip is now a silent defer.

Built through the tdd-pipeline (commit `c30160f`). Gate
`GATE PASS: Core + Unity EditMode green (passed=989 failed=0 skipped=0)` (`GATE_FORCE_UNITY=1`).

Live-verified via `unity-live-verify` (`SceneBuilderTest/Logs/live-verify-spec50.log`), 3/3 with auto ON:
(1) a source change enqueued while the scene was closed ran a pump tick that left the path PENDING (not
dropped), then executed and moved the marker to (1,2,3) once the scene opened; (2) an atomic replace
(temp + rename) converged the scene to (7,8,9) at parity with a plain append, and a focus-regain rescan
converged a watcher-slipped write to (4,5,6) with no in-scene edit; (3) an `EnteredPlayMode`->`EnteredEditMode`
round trip through `OnPlayModeStateChanged` left `IsArmed=True` and a real edit drove a scene->code cycle
through natural update ticks with no manual reload. The "did not open after 10 attempts — abandoned" lines
were fix #1's retry ceiling firing because the harness held scenes closed past the ceiling, not a defect.

## 51 - the authoring surface accepts natural forms

Three parse/recognize/resolve gaps that rejected valid intent are closed (recognizer + parser moved
together, pinned by the agreement tests):
- **A — instance component surface.** `InstanceHandle`/`InstanceHandle<TRef>` gained
  `Component<T>()`/`Component<T>(configure)` (an alias for `AddComponent<T>`), and instance verbs now
  dispatch on a CAPTURED instance handle used in a later statement, not only inline on the `Instance(...)`
  call — the setter-only re-dispatch routes an instance-verb call on an instance-typed receiver through
  the same per-verb lowering `ProcessInstanceChain` uses. No instance receiver reaches the SB1002
  `default` arm.
- **B — sub-asset type disambiguation.** `TryResolveSubObject` gained an expected-type parameter threaded
  from the target `SerializedProperty`; a bare sub-name matching several sub-objects filters to the one
  assignable to the field type (so `m_Mesh` picks the `Mesh` named `start`, not the `Transform`/`MeshFilter`).
  Only a still-ambiguous match after the type filter is a located error.
- **C — const-string path folding.** One constant-string folder that all three literal-demanding sites
  consult (recognizer `IsStringLiteral`, parser `EvalStringLiteral`, `ValueNodeParser.TryStringLiteral`);
  `ParseCore`/`Analyze` first collect the const-string environment (class-level `const string` fields and
  Build-body `const string` locals) and thread it in. The folder evaluates a literal, a reference to a
  known const string, and a `+` concat of such constants; anything non-constant is still refused.

Built through the tdd-pipeline (commits `9dae215` gaps A+C, `34b3a10` gap B). Gate
`GATE PASS: Core + Unity EditMode green (passed=995 failed=0 skipped=0)` (`GATE_FORCE_UNITY=1`).

Live-verified via `unity-live-verify` (`SceneBuilderTest/Logs/live-verify-spec51.log`): (A) inline,
captured-statement, and multi-statement-configure instance forms all built real Kenney fbx instances
carrying `Rigidbody`/`SphereCollider` (radius 0.5, isTrigger) with no "Unsupported builder call"; (B)
`Asset("start.fbx","start")` into `m_Mesh` resolved to the `Mesh` sub-object past the
GameObject/Transform/MeshFilter/MeshRenderer name collision, and a same-type collision (two Meshes named
`start`) still threw a located `SB2101` with the scene untouched; (C) a class-level `const string Kit` +
a Build-body `const string` local folded through `Instance(Kit + ...)`, `Add(Grp + ...)`, and
`Asset(Kit + ..., "start")`, while a non-const `prefix +` was still refused located (`SB1000`). Console
clean of product errors.

## 52 - AlignTo: generalize + rename SurfaceSnap to per-axis extent alignment

`SurfaceSnap` (which only expressed outside-face contact, `mine + theirs = 1`) is renamed to `AlignTo`
and generalized to a named per-axis alignment. Per axis X/Y/Z the author picks a `Mode`
(`None`/`AbutMin`/`AbutMax`/`AlignMin`/`AlignMax`/`AlignCenter`) with an optional world-unit
`.Offset(...)`. `AbutMin`/`AbutMax` are the old contact directions (Up/Down/Left/Right/Forward/Back all
collapse to these two per axis); `AlignMin`/`AlignMax` are the new same-side face-flush cases (bottoms
or tops aligned), `AlignCenter` centers. Evaluated in a frame that defaults to the TARGET's local space
(so a rotated/sloped target aligns along its own axes, not a world box), with `space: World` and a
`frame:` transform override; the frame math reuses spec 45's `ProjectedExtent` kernel. `None` at enum
index 0 keeps the unpinned-axis default-prune. No-target raycast/scan supports `AbutMin`/`AbutMax` only;
`Align*` without a target is a located error. Live re-snap, `captureThreshold` detach, `PositionAuthority`
arbitration, and execution order -90 are preserved. Clean rename, no back-compat alias (pre-launch); a
guard test fails if `SurfaceSnap` reappears in tracked source.

Built through the tdd-pipeline (commits `fed9659` feat, `9c4d712` docs) plus a live-verify follow-up fix
`1259a59` (see below). Gate `GATE PASS: Core + Unity EditMode green (passed=1006 failed=0 skipped=0)`
(`GATE_FORCE_UNITY=1`).

Live-verified via `unity-live-verify` (`SceneBuilderTest/Logs/live-verify-spec52.log`): regression
(`AbutMax` rests the bottom face on the floor top; no-target raycast lands); the minigolf case
`AlignTo(green, x: AlignCenter, y: AlignMax, z: AbutMax)` placed with tops co-planar, X-centered, and
abutting past the green's max-Z, each read from live bounds; `AlignMin`/`AlignCenter` and a
`.Offset(0.5f)` gap confirmed; the FRAME default followed a 30deg-tilted target's own up
(`(-0.158, 0.275, 0)`) while `space: World` gave world Y (`(0, 0.866, 0)`); a no-target `AlignMax`
threw a located `SB1000`; and `SurfaceSnap` no longer resolves as a symbol. Live-verify also caught a
real emission defect (an introduced/edited per-axis offset synced back as a non-compiling bare
`zOffset:` keyword instead of folding into `z: AxisAlign.AbutMax.Offset(...)`) which the product's own
`BuilderCompileCheck` flagged; fixed RED-first in `1259a59` by folding the axis (mode, offset) into one
rendered argument shared by the append and incremental paths, with Core + EditMode regression tests.

## 53 - the spatial solvers size and place instanced prefabs (whole-hierarchy bounds)

The "drop it in, size/rest by relationship, never hand-measure" workflow broke for the most common
real asset: a downloaded prefab instanced with `scene.Instance(...)`. Surfaced by the house one-shot
(the author fell back to a hand-written `PropMeasure.cs` that walked the prefab's children). Three
stacked defects fixed: (1) `FitSize`/`AlignTo`/`Between` read a Renderer/MeshFilter on their OWN
GameObject only, so an instanced prefab whose mesh lives in child nodes couldn't be sized/placed — the
solvers now resolve their extent from the WHOLE hierarchy (`GetComponentsInChildren`, combined in the
solver object's local space so rotation isn't inflated) via the shared `ProjectedExtent` kernel, so all
three inherit it; a single-mesh node is byte-identical to before. (2) The solver verbs didn't exist on
`InstanceHandle` and the fluent form was silently discarded (parser stored into `node.Components`, which
`BuildInstanceNode` throws away) — the verbs are now on `InstanceHandle` and route to `AddedComponents`.
(3) A builder call on an instance that maps to no destination is now a located error, not a
clean-console no-op.

Built through the tdd-pipeline (commits `3ff46f1`, `0bec67f`). Gate
`GATE PASS: Core + Unity EditMode green (passed=1017 failed=0 skipped=0)` (`GATE_FORCE_UNITY=1`).

Live-verified via `unity-live-verify` (`SceneBuilderTest/Logs/live-verify-spec53.log`), 5/5 against a
real nested-mesh model (`Kenney/CarKit/sedan.fbx` — 0 renderers on the root, 5 in children):
`scene.Instance(sedan).FitSize(width: 2f).AlignTo(floor, y: AbutMax)` produced a live combined-bounds
width of exactly 2.0000 m and rested the combined-bounds bottom exactly on the floor top (0.00000 gap),
with `FitSize`/`AlignTo` components actually present on the instance (not dropped); a 45deg-rotated
instance solved scale 0.6984, matching the oriented-extent projection (not a world-AABB); an unrouted
`.Bogus()` gave a located `SB1000` with the scene untouched; and the re-sync was byte-stable
(`PatchEdits=0`). Console clean of product errors. The `PropMeasure.cs` workaround is now unnecessary.
