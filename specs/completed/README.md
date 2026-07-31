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

31 and 32 are the two entries here that are NOT live-verified. 31 (the reader must surface every
field Unity hides from the default inspector, not just what NextVisible draws) landed gate-green at
458 and recovered component enable/disable syncing plus Canvas/Rigidbody/MeshRenderer hidden state,
but its live confirmation never ran.

32 is CLOSED NARROWED. It delivered its two
owners — the per-type default template (C2+C3) and the value representation contract (C1+C4), plus
`ValueWalk.cs` as the single walk every value path uses — at `passed=517 failed=0`, mutation-checked,
with every prior test surviving unweakened. But it is gate-verified only, and every one of its six
defect classes was originally found in a live editor while the gate sat green at 458 tests. Read its
"Verification status" section before trusting it. C5, C6 and the round-trip proof suite that would
close that gap moved to `specs/33`; partial, explicitly unvalidated work on them sits in `795eba2`.

Still pending in `specs/`: 08 (M7 robustness, rescoped), 09 (M8 UnityEvents, reframed to typed
method-lambda), 10 (M9 SerializeReference), 12 (M11 animation, blocked on Animator research),
33 (boundary defects C5/C6 + bidirectional round-trip proof).
`00-foundation.md` stays in `specs/` as the living base contract.
