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
SerializeReference), 12 (M11 animation, blocked on Animator research) and 34 (licensing, blocked on
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
