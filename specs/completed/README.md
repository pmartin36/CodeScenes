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

Still pending in `specs/`: 08 (M7 robustness, rescoped), 09 (M8 UnityEvents, reframed to typed
method-lambda), 10 (M9 SerializeReference), 12 (M11 animation, blocked on Animator research),
35 (reference-list writes producing null slots, the sorting-layer convergence regression, incremental
snapshot-cache invalidation, and a typed selector sync can emit non-compiling). `00-foundation.md`
stays in `specs/` as the living base contract.
