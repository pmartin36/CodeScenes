# Spec 37 — RequireComponent(RectTransform) hosts must not drift a phantom .RectTransform(...)

## Measured defect (do not re-derive)

A `UnityEngine.UI.Slider` (or any `[RequireComponent(typeof(RectTransform))]` host, e.g. any UI
`Graphic`) authored via generic `.Component<Slider>()` with NO explicit `.RectTransform(...)` call
drifts scene->code on the FIRST sync over a converged build. It injects an unauthored
`.RectTransform(anchoredPos:(0f,0f), sizeDelta:(100f,100f), anchorMin:(0.5f,0.5f),
anchorMax:(0.5f,0.5f), pivot:(0.5f,0.5f))` where every value equals the RectTransform default, then
converges (sync 2 is a fixed point). This breaks the shipped invariant "the first sync over a
converged build is a no-op" and writes a `.RectTransform(...)` the author never typed.

The mechanism, verified in code:

- The parser sets `IsRectTransform` only on an explicit `.RectTransform()` call
  (`SceneBuilder.Core/Parsing/BuilderParser.RectTransform.cs:16-19`); the default transform Kind is
  plain `Transform` (`SceneBuilder.Core/Model/TransformData.cs:8`). A `.Component<Slider>()` node
  therefore stays model-side `Kind=="Transform"`.
- The live transform IS a `RectTransform` (Slider's `RequireComponent`), so the snapshot reads
  `Kind=="RectTransform"` (`com.codescenes/Editor/LiveTransformRead.cs:43,47-51` via
  `SceneSnapshotReader.cs:239`).
- The differ sees the Kind mismatch (model `Transform` vs snapshot `RectTransform`) and takes the
  PROMOTION arm, emitting `SetRectTransform{Changed=ChannelMask.None}`
  (`SceneBuilder.Core/Diff/RectTransformDiff.cs:49-56`). The MATCHED arm that omits-at-default is
  `RectTransformDiff.cs:73-78` (`ChangedChannels`); the promotion bypasses it.
- Scene->code emission then emits one `PatchArgument` per non-driven field, value = snapshot ??
  Default, EVEN when it equals the field default (deliberate per spec 13 D6:
  `SceneBuilder.Core/Reconcile/Reconciler.RectTransform.cs:19-22` intent, `:33-52` the promotion arm,
  `:73-96` `EmitRectTransformEdits`; dispatched from `Reconciler.cs` `case SetRectTransform`). A plain
  Slider has no driven rect channels, so all five free fields emit.

Generic rect defaults live at `SceneBuilder.Core/Model/RectTransformFields.cs:34-38`. Nothing today
recognizes the component-supplied rect as "at default": `com.codescenes/Editor/ComponentDefaultTemplate.cs:108`
harvests only a component's own serialized fields, never the sibling auto-added RectTransform.

The build order note: this is a spec-13 follow-on defect, filed here rather than reopening spec 13.
It is not data loss (the scene value is correct and the sync converges on the second pass), but it
violates the fixed-point invariant on the happy path and forced an M8 workaround (see the regression
re-tightening below).

## Goal

A node whose component set requires a `RectTransform` and carries no explicit `.RectTransform(...)`
call reaches a fixed point on the FIRST sync over a converged build: zero `SetRectTransform` ops, no
`.RectTransform(...)` injected. The fix removes the Kind mismatch at its source so the differ takes
the matched omit-at-default arm.

## In scope

- An adapter->Core signal that answers "does this component `TypeRef` carry
  `[RequireComponent(typeof(RectTransform))]`?", threaded into desired-model construction.
- A desired node hosting any require-RectTransform component is materialized and diffed as
  `Kind=="RectTransform"` even with no explicit `.RectTransform(...)`.
- All-default rect layout on such a node -> ZERO `SetRectTransform` ops. Non-default layout (the
  author moved or resized it in the editor) -> emit ONLY the changed channels via the existing
  matched omit-at-default arm.
- Restoring M8's Item7 assertion from `ProveCodeToScene` to `ProveCodeToSceneFixedPoint` once green.

## Out of scope

- Any change to the spec-13 D6 promotion arm (`Reconciler.RectTransform.cs:19-22`,
  `RectTransformDiff.cs:49-56`). It is not modified. It simply stops firing for require-component
  hosts because their desired Kind now matches the snapshot, and it remains for a genuinely
  unexpected promotion.
- An explicit `.RectTransform(...)` call. It already sets `Kind=="RectTransform"` and is unchanged.
- Driven-channel masking behavior. An auto-promoted RectTransform participates in driven-masking
  exactly like an explicitly-authored one; no special case is added.
- `[RequireComponent]` for any type other than `RectTransform`. Only the RectTransform requirement
  affects transform Kind; other required components are ordinary M3 components and are not this
  spec's concern.

## Additions to the contract

No new Core model type. The fix reuses `TransformData.IsRectTransform` / `Kind` (§3) exactly as
spec 13 (M-UI) defined them. A require-RectTransform host's desired node simply carries
`Kind=="RectTransform"` with its five `Vec2` fields at `RectTransformFields` defaults, which is the
same shape a bare `.RectTransform()` produces.

One adapter->Core signal is added: a predicate the Editor supplies, answering whether a component
`TypeRef` requires a `RectTransform`. Core is Unity-free (§2) and cannot read a `RequireComponent`
attribute, so the answer is computed adapter-side by reflection and threaded into desired-model
construction. This is a signal into the existing desired-model seam, not a new POCO on the model.

## Core deliverables

Each is a testable contract.

- **A desired node flagged as hosting a require-RectTransform component is promoted to
  `Kind=="RectTransform"`.** When the desired-model construction is told a node's component set
  includes at least one require-RectTransform type, and the node has no explicit `.RectTransform(...)`
  call, the node's `TransformData` is set to `Kind=="RectTransform"` with the five fields at
  `RectTransformFields` defaults, before any diff. A node whose components require no RectTransform is
  untouched.
- **All-default rect layout -> zero ops.** A desired node so promoted, diffed against a snapshot whose
  `Kind=="RectTransform"` and whose five fields equal `RectTransformFields` defaults, yields ZERO
  `SetRectTransform` ops. The differ reaches the matched arm (`RectTransformDiff.cs:73-78`,
  `model.IsRectTransform==true`), not the promotion arm.
- **Non-default rect layout -> changed channels only.** The same promoted node diffed against a
  snapshot whose rect layout differs from default in one or more channels emits ONE `SetRectTransform`
  carrying ONLY the changed channels, and scene->code emits a `.RectTransform(<changed>)` with the
  real values via the existing matched omit-at-default arm. No unchanged channel is written.
- **Explicit `.RectTransform(...)` and an auto-required rect agree.** A node authored with an explicit
  `.RectTransform(...)` on a require-RectTransform host is `Kind=="RectTransform"` from the parse
  (`BuilderParser.RectTransform.cs:18`) and is not double-handled by the promotion: both paths set the
  same Kind, so there is no conflict and the explicit values remain authoritative.
- **Driven masking is unchanged.** A promoted RectTransform whose fields the adapter marks driven is
  masked out of diff and write-back exactly as an explicitly-authored RectTransform is (spec 13
  driven-detection). No new driven logic is added.

## Editor adapter deliverables

> Built by the pipeline, gated by the Unity EditMode suite in `unity-gate/`. Not hand-wired.

- **The require-RectTransform predicate (the cross-cutting mechanism).** An Editor-side predicate that,
  given a component `TypeRef`, resolves the component `System.Type` and answers whether it carries
  `[RequireComponent(typeof(RectTransform))]`, transitively over its `RequireComponent` attributes
  (a `RequireComponent` may name up to three types per attribute, and a type may carry several). It
  resolves the type through the same resolver the adapter already uses for a `TypeRef`
  (`ComponentTypeResolver`), and returns false for a type that does not resolve rather than throwing.
- **Threading into the desired-model seam.** The predicate is applied inside
  `com.codescenes/Editor/DesiredModelLoader.Load` (the ONE seam that turns builder source into the
  desired model for BOTH directions), as a stage that runs before the returned `Desired` model is
  handed to any Diff/Materialize/Reconcile. Applying it there means every consumer inherits the
  promotion by default: neither `SceneBuilderBuild` (code->scene) nor `SceneBuilderSync` (scene->code)
  can obtain a desired model that skipped it. A node with an explicit `.RectTransform(...)` is already
  `Kind=="RectTransform"` and the stage leaves it unchanged.

## Decomposition guidance

**The cross-cutting invariant is the require-RectTransform signal, and it gets ONE owning task and ONE
shared mechanism every consumer calls** (per foundation §5's invariant-hoisting rule and the M-UI
lesson that a rule stated per-caller regresses the moment a caller forgets it). The promotion must be
a stage inside `DesiredModelLoader.Load`, not a call each direction opts into. `SceneBuilderBuild` and
`SceneBuilderSync` both obtain their desired model only from `DesiredModelLoader.Load`, so applying
the promotion there is the structural placement that no future caller can bypass.

Likely tasks:

- **(a) The adapter predicate + desired-model threading (the invariant owner).** Owns the reflection
  predicate and its single application point in `DesiredModelLoader.Load`. TOUCHES the new predicate
  source, `com.codescenes/Editor/DesiredModelLoader.cs`, and whatever Core-side hook receives the
  per-node "requires RectTransform" flag during desired-model construction. If the promotion is
  computed in a Core helper the adapter feeds, that helper file is in this task's TOUCHES; if it is
  computed adapter-side and stamped onto the model, keep the stamping in TOUCHES. Do not split the
  predicate from its application point across two tasks: an owned defect whose signal and consumer
  live in different tasks reintroduces the opt-in hazard.
- **(b) The differ/reconcile consuming `Kind=="RectTransform"` for auto-required hosts.** This is
  mostly a consequence of (a): once the desired node carries `Kind=="RectTransform"`,
  `RectTransformDiff.EmitEdit` (`RectTransformDiff.cs:73-78`) and `Reconciler.RectTransform.cs:55-60`
  (the matched arm) already do the right thing. If any diff/reconcile site special-cases a Kind
  mismatch in a way that would still fire for a promoted node, it is fixed here and its file is in
  TOUCHES.
- **(c) Tests.** The Core RED cases and the adapter EditMode cases below, plus the M8 regression
  re-tightening.

Enumerate the RED cases literally so a symmetric set is not pruned: the all-default case AND the
non-default case in Core; the code-authored Slider fixed-point case AND the in-editor rect-edit case
in EditMode. A test-only task gets no code-writer; keep each task's TOUCHES complete or split it.

## Core test plan (RED, headless)

`SceneBuilder.Core.Tests` (xUnit, §8). Behaviors, not implementation:

- `Diff_RequireRectHost_AllDefaultLayout_ProducesNoSetRectTransform`. A desired node
  `Kind=="Transform"` flagged as hosting a require-RectTransform component, once run through the
  promotion, diffed against a snapshot `Kind=="RectTransform"` whose five fields equal
  `RectTransformFields` defaults, yields ZERO `SetRectTransform` ops. Today this path emits one
  promotion op; the test is RED until the promotion runs.
- `Diff_RequireRectHost_NonDefaultLayout_EmitsOnlyChangedChannels`. The same promoted node diffed
  against a snapshot whose rect layout is non-default in one or more channels emits ONE
  `SetRectTransform` carrying only the changed channels, and scene->code emits a
  `.RectTransform(<changed>)` with the real values; no unchanged channel is written.
- `Promotion_ExplicitRectTransform_IsNotDoubleHandled`. A node authored with an explicit
  `.RectTransform(...)` on a require-RectTransform host stays `Kind=="RectTransform"` with the authored
  values, and the promotion stage does not overwrite them with defaults.

## Unity confirmation checklist (EditMode, `unity-gate/`)

Required, per the Unity-facing coverage rule: this is adapter behavior at the
`RequireComponent`/`RectTransform` boundary.

1. **Code-authored Slider is a first-sync fixed point.** Author
   `scene.Add("Slide").Component<UnityEngine.UI.Slider>()`, Build, then Sync over the unchanged scene.
   *Expected:* `SyncResult.PatchEdits == 0` and the builder source is byte-unchanged. No
   `.RectTransform(...)` is injected.
2. **An in-editor rect edit emits only the changed channels.** From the same built scene, drag or
   resize the Slider's RectTransform in the editor, then Sync. *Expected:* a `.RectTransform(...)` is
   introduced (or patched) carrying the real edited values (floats f-suffixed via
   `SourceExpr.Vec2Literal`), and only the channel(s) the author changed appear; a second Sync is a
   no-op.

## Regression re-tightening

Once this spec is green, restore M8's Item7 call site from `ProveCodeToScene` back to
`ProveCodeToSceneFixedPoint` at `unity-gate/Assets/GateTests/UnityEventRoundTripTests.cs:250-266`
(the `Item7_SliderOnValueChangedDynamicToHudSetValue_SourceIsOnEventDynamic_RoundTrips` test and its
preceding comment block), so the Slider round-trip proof asserts a true first-sync fixed point again.
That workaround exists only because the Slider fixture's RectTransform drifts on the first sync; this
spec removes the drift, so the weaker convergence check is no longer needed and its comment is
deleted with it.

## Dependencies

- **Spec 13 / M-UI (RectTransform)** — `TransformData.Kind`, the five `Vec2` fields,
  `RectTransformFields`, `SetRectTransform`, the matched omit-at-default arm, and D6's promotion arm.
  This spec builds directly on M-UI's Kind machinery and changes none of it.
- **M0** — differ and Plan.
- **M2** — Reconcile and `PatchArgument` write-back.
- **The desired-model seam** — `DesiredModelLoader.Load`, the single point both directions load the
  desired model through.

## Risks/notes

- **Do not "default-omit in the promotion arm."** Making `Reconciler.RectTransform.cs:33-52` skip a
  field that equals its default would silently drop a genuine Kind change for a truly unexpected
  promotion, which spec 13 D6 forbids. The fix is upstream: make the desired Kind match the snapshot
  so the promotion arm never fires for a require-component host, leaving D6 intact for the cases it
  was written for.
- **Transitive `RequireComponent`.** A UI component may require its RectTransform indirectly (a type
  requiring another type that requires RectTransform). The predicate walks the requirement chain, not
  just the immediate attributes, so an indirect UI host is promoted too. Cover at least one directly-
  and one indirectly-requiring type if the enumeration is cheap.
- **Type that does not resolve.** A `TypeRef` the adapter cannot resolve (missing assembly, deleted
  script) answers false and is not promoted, rather than throwing inside the desired-model load. That
  node keeps `Kind=="Transform"`; if it truly hosts a RectTransform live, the pre-existing promotion
  path still fires as it does today. The fix narrows the phantom to zero for resolvable require-hosts,
  which is every UI component that ships.
