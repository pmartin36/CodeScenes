# Between + frame-aware spatial kernel (revises 19/23)

**Status:** spec for a future build. Revises `specs/completed/19-spatial-authoring-components.md` and
builds on `specs/completed/23-fitsize-surfacesnap-transform-authority.md`. Two coupled changes:

1. A new editor-time intent component, **`Between`**, that places an object at a `fraction` along one
   named axis, flush-bumping between two anchor objects at `fraction=0` and `fraction=1`.
2. The spatial family's geometry kernel changes from **world-AABB face math** to a **projected-extent
   kernel** that measures an object's extent along an arbitrary axis direction. `Between` requires it;
   `SurfaceSnap`/`FitSize` adopt it. The change is backward-compatible: axes are world by default, and
   the projected extent degenerates to the AABB face when nothing is rotated, so existing untilted
   scenes produce identical geometry (a gate assertion).

A second position-driver now exists (`SurfaceSnap` was the only one), so per-axis transform authority
gains a conflict rule: an authoring-time analyzer diagnostic plus a deterministic runtime arbitration.

## Why

Everything in the family keys off `Renderer.bounds`, a world axis-aligned box, correct only for upright
objects in an untilted scene. `Between` needs to place along one axis while **ignoring the other two**
(two anchors at different heights lerp along X with their height difference discarded), and it needs to
optionally follow a rotated axis when the content is tilted. World-AABB face math cannot express either.
One projected-extent kernel, plus an optional orientation reference, covers both.

Out of scope, and NOT part of this spec or of `SurfaceSnap`: reorienting an object to lie flush *on* an
inclined surface (bottom face parallel to a ramp). That drives **rotation**, is a distinct future
component (working name `OrientToSurface`), and is only *reserved for* here (see Authority), not built.

## The `Between` component

### Authoring API (`com.codescenes/Runtime/NodeHandle.cs`)

```csharp
public NodeHandle Between(
    SceneObjectHandle from,          // the fraction=0 anchor (required)
    SceneObjectHandle to,            // the fraction=1 anchor (required)
    float fraction,                  // 0..1 along the corridor (required; unclamped)
    Between.Axis axis,               // which axis to slide along (required)
    SceneObjectHandle alongOrientationOf = null) // orientation reference; null => world axes
    => this;
```

Named arguments only (the spatial parse arms reject positional args). Example:
`node.Between(from: leftPost, to: rightPost, fraction: 0.25f, axis: Between.Axis.X)`.

**This method is an owned deliverable, not just parser input.** The build path parses source *text*, so
the parse/round-trip tests pass whether or not `NodeHandle.Between` actually exists. `NodeHandle.cs`
(the file that declares `FitSize`/`SurfaceSnap` today) MUST be in the owning task's `TOUCHES`, with a
deliverable that (a) `node.Between(from:, to:, fraction:, axis:, alongOrientationOf:)` compiles as an
authoring call, and (b) it carries an XML doc comment (published API copy — `SceneBuilder.DocGen` emits
it to `api.json`; verify the entry appears). The natural owner is the task that also adds the
`Between.Axis` enum the signature references.

### Semantics

- **Axis is world by default; single-axis projection.** `axis` selects a direction. With
  `alongOrientationOf` null it is a world axis (`X` = world x); with a reference set it is that
  reference's corresponding local axis (its rotation carries the tilt). The object is translated **only
  along that direction**. Its position on the other two axes is never touched (free, or owned by another
  component). Both anchors and the object are used **only by their projection onto the axis** — anything
  perpendicular (e.g. a height difference between the anchors) is discarded by construction.
- **Hierarchy-independent.** Anchors contribute their world position and world bounds only. Where they
  live in the scene tree is irrelevant; two anchors in different branches, or under different parents,
  are fine. The axis direction comes from world (or the single `alongOrientationOf` reference), never
  from the anchors' parents.
- **The corridor.** Project `from` and `to` onto the axis. `from`'s face toward `to` and `to`'s face
  toward `from` bound a corridor. At `fraction=0` the object's `from`-facing face is flush against
  `from`; at `fraction=1` its `to`-facing face is flush against `to`. Travel = corridor length minus the
  object's own extent along the axis. `fraction` linearly interpolates position across that travel.
  **Flush is measured along the axis only:** at `fraction=0` the object's X-face lines up with `from`'s
  X-face even if the two are at different heights and never touch in 3D. That is the correct reading of
  "ignore the perpendicular," not a bug.
- **Anchor order is fixed by argument, not position.** `from` is always the `fraction=0` end even if it
  currently sits on the higher-coordinate side, so `fraction` stays stable when objects move during
  sync. Facing faces are chosen by the sign of `(to - from)` projected onto the axis.
- **`fraction` is unclamped.** Negative places the object past `from` (away from `to`); `>1` places it
  past `to`. Intended (the original "negative %" request).
- **Degenerate corridor: warn, do not fail.** If the object's axis extent exceeds the corridor, flush-
  at-0 and flush-at-1 cross and `fraction` interpolates a negative corridor (object overlaps both
  anchors). Allow it and emit a one-time `Debug.LogWarning` (mirror `FitSize`'s degenerate warning).
- **Required renderers.** The object and both anchors need `Renderer` bounds (anchors reuse
  `SurfaceSnap`'s target-renderer path). Missing renderer -> one-time `Debug.LogError`, no move.
- **Cyclic anchors.** Reject self-reference (`from`/`to`/`alongOrientationOf` == self). Longer cycles are
  last-writer/undefined and not detected in this spec; document the limitation.

### Sync-back: back-solve `fraction` (not detach)

`SurfaceSnap` detaches on a large drag because its parameter is a discrete direction. `Between`'s
parameter is continuous, so it has a natural inverse: when the user drags the object along the axis,
read the new position back into `fraction` (drag to 40% of the corridor -> source updates to
`fraction: 0.4f`), exactly as `FitSize` back-solves its `value` from a manual rescale. Off-axis drags
are free. The raw transform channel is never written back to source; only `fraction` is. It may
back-solve to a negative / >1 value, consistent with the unclamped forward path.

### Data model and Core contract (`SceneBuilder.Core/Model/SpatialComponents.cs`)

Add, mirroring the `FitSize`/`SurfaceSnap` blocks:

- `BetweenTypeName = "SceneBuilder.Authoring.Between"`.
- `BetweenFields`: `From = "from"`, `To = "to"`, `Fraction = "fraction"`, `Axis = "axis"`,
  `Orientation = "orientation"` (authoring keyword `alongOrientationOf`).
- `BetweenEnums`: `AxisTypeName = "SceneBuilder.Authoring.Between+Axis"`, members `X`/`Y`/`Z`.
- **`BetweenDrivenMask(Between.Axis axis, bool oriented)` is the single owner of which channels a
  `Between` drives**, and every consumer (parse's `DrivenChannels |=`, the emit-suppression path, and
  the live driven-read path) MUST route through it — none may open-code the mask. It returns the one
  world position bit for the axis when `oriented` is false (X->PositionX, Y->PositionY, Z->PositionZ),
  and the full `PositionX | PositionY | PositionZ` mask when `oriented` is true (an `alongOrientationOf`
  axis of travel spans all three world components). A gate test asserts the emit-suppression mask and
  the live driven-read mask for the same component are byte-identical, so a bypassing site that lets the
  two paths disagree fails the gate (this is the round-trip divergence the Authority section warns of).

The runtime component `com.codescenes/Runtime/Between.cs` mirrors these field/enum names byte-for-byte
(the by-name write contract), with `[ExecuteAlways]` and `[DefaultExecutionOrder(-80)]` (after
`FitSize(-100)` so size is settled, after `SurfaceSnap(-90)`). Custom inspector
`com.codescenes/Editor/BetweenEditor.cs` (matching `SurfaceSnapEditor.cs`/`FitSizeEditor.cs`):
two object fields, a `fraction` float, an axis dropdown, an optional orientation-reference field. It is
editor-only, stripped from builds by `SpatialBuildStripper`, and excluded from live transform read by
`SerializedFieldExclusions`/`LiveTransformRead`, same as `FitSize`/`SurfaceSnap`. `PlanExecutor` calls a
`ResetBaseline()` hook after it writes `m_LocalPosition` (same coordination pattern as the other two).

### Parse / emit seams

- **Parse:** add `ApplyBetween` to `BuilderParser.Spatial.cs`. `from`/`to`/`alongOrientationOf` parse via
  `ValueNodeParser.Parse` (ObjectRef, total). `fraction` reuses `ParseSpatialScalar`. `axis` parses to a
  canonical `ValueNode.Enum` over `BetweenEnums.AxisTypeName`. On parse, `node.DrivenChannels |=` the
  driven mask (see Authority for the oriented case).
- **Emit:** add a `Between` arm to `SpatialComponentSource` (`IsSpatial` returns true for it). Render
  `.Between(from:, to:, fraction:, axis:, alongOrientationOf:)`, omitting `alongOrientationOf:` when it
  is the default (world). **`fraction` and `axis` are exempt from default-value pruning:** `fraction=0`
  is a meaningful placement and `axis=X` may be enum index 0, and both MUST always emit or the round-trip
  loses the placement/axis.

## Frame-aware projected-extent kernel

Replace the world-AABB face computation (`SurfaceSnap.ResolveAndApplyAxis` and its `FitSize` equivalent)
with one shared kernel:

- **Projected extent (support function).** For an object with local bounds and a world rotation, its
  extent along a unit axis direction `d` is `sum over i of |halfExtent_i * dot(worldBasis_i, d)|`. This
  degenerates to the world-AABB min/max face when `d` is a world axis and the object is unrotated (the
  common case), so correctness for the general case is free, not a caveat. The flush offset against an
  anchor is computed by projecting the anchor's extent the same way.
- **Axis direction.** World by default; when `alongOrientationOf` is set, `d` is that reference's local
  axis. World `d` == today's behavior, so `SurfaceSnap`/`FitSize` are unchanged in untilted scenes
  (**zero regression on every shipped scene is a hard requirement and a gate assertion**).
- **Adoption.** Build the kernel for `Between` first (it cannot fake it), then migrate `SurfaceSnap` and
  `FitSize` onto it. `SurfaceSnap`'s raycast rest-on-surface path is unchanged in behavior; only its
  face/extent math routes through the kernel.
- **Recompute triggers.** `Between` re-evaluates when either anchor moves (watch BOTH), when the object's
  own rotation changes (world-AABB ignored rotation; projected extent does not), and when
  `alongOrientationOf`'s rotation changes.

## Per-axis authority and conflict (extends spec 23)

Two intent components can now drive position on one object. Different axes compose (`SurfaceSnap` the
floor on Y, `Between` the corridor on X). The same axis conflicts.

- **Authoring-time analyzer diagnostic** (`CodeScenes.Analyzers`). Emit a diagnostic when two
  axis-claiming position intents on one node resolve to the same axis (both literal-arg axes are
  statically known: `SurfaceSnap` from which side is set, `Between` from its `axis` arg). This is the
  "compiler flag" catch, surfaced as the user types.
- **Runtime arbitration.** Even with the diagnostic, two same-axis drivers physically fight each frame.
  Resolve deterministically: a stable priority (or execution order) picks one owner per axis, and the
  loser emits a **one-time** `Debug.LogWarning` naming the conflict. Never oscillate silently.
- **Different orientations are conservative.** Two position-drivers using different orientation
  references cannot be proven orthogonal statically; treat overlap as a conflict and warn at runtime.
  In the common all-world case this never triggers.
- **ChannelMask ownership (round-trip).** Both the emit-suppression path and the live driven-read path
  get the owned channels from the one `BetweenDrivenMask(axis, oriented)` helper defined in the data
  model above — never open-coded. World-axis case (`oriented` false): `Between` owns one world position
  bit and the emit suppresses re-emitting that one channel as `.Transform(pos:)`. Oriented case
  (`alongOrientationOf` set, `oriented` true): the axis of travel spans multiple world position
  components, so the mask is the whole position and `Between` owns all of it for emit (do not co-author a
  perpendicular `pos:`); the scene transform is still written in full per spec 23. The two paths sharing
  one helper is what keeps them from disagreeing at the seam.
- **Rotation channels reserved, not built.** Add `RotationX/Y/Z` flags to `ChannelMask` so the authority
  model can express a rotation-driving intent. Nothing here drives rotation; this only reserves the seat
  for a future `OrientToSurface` so it is not precluded.

## Gate coverage (EditMode, through the real Build + drive path)

Per CLAUDE.md, adapter behavior is not covered by direct `Evaluate()` on hand-placed objects (the exact
gap that let spec 19's write-skip bug escape). Add EditMode tests in `unity-gate/Assets/GateTests/` that
run `SceneBuilderBuild.Run` + live component drive and assert observed geometry:

- `Between(from, to, fraction)` at `0`/`0.5`/`1` -> object flush-against-from / centered /
  flush-against-to, bounds not overlapping the anchors at the endpoints.
- Negative `fraction` and `>1` -> object past the respective anchor.
- **Height ignored:** two anchors at different Y, axis X -> object lerps along X, its Y untouched, and
  the endpoint flush is along X regardless of the Y offset.
- **Oriented axis:** anchors + object under a 45deg-rotated root with `alongOrientationOf: root` ->
  placement follows the tilted axis; a co-authored `SurfaceSnap` on a different axis still works.
- **Anchors in different branches** of the tree -> placement is correct (hierarchy-independent).
- **Zero regression:** an untilted `SurfaceSnap`/`FitSize` scene produces byte-identical geometry after
  the kernel swap.
- Drag along the axis -> `fraction` back-solves; drag off-axis -> free.
- Degenerate corridor (object wider than the gap) -> placed + one warning, no error.
- Same-axis conflict (`SurfaceSnap` and `Between` both on X) -> deterministic result + one warning; plus
  an analyzer test asserting the authoring diagnostic fires.

## Out of scope (named so the boundary is explicit)

- `OrientToSurface` / align-to-normal (lie flat on a ramp) — drives rotation; future component.
- `SurfaceSnap` reorienting to a non-axis-aligned surface — unchanged; it rests-on-top via raycast and
  does not reorient.

## Resolved decisions (ratified by the user)

1. **`fraction`, 0..1, unclamped.** Not `t` (reads as time) or `percent` (fights a 0..1 value).
2. **Named single axis, not the line between anchors.** Project onto one axis and discard the
   perpendicular, so a height difference between anchors is ignored. This is the core requirement.
3. **Axes are world by default.** Optional `alongOrientationOf:` reference supplies a rotated axis when
   the content is tilted. No per-parent frame; hierarchy-independent.
4. **Emit ownership:** world case suppresses the one owned position channel; oriented case, `Between`
   owns the whole position for emit.

## Dependencies

- Revises spec 19; builds on spec 23's transform-first authority and repurposed `ChannelMask`.
- Reuses M3 (components/fields), M1/M2 (transform materialize/reconcile), the `ComponentReconciler` emit
  path, the build-strip (`IProcessSceneWithReport`), and the reference machinery of specs 35/43 for the
  `from`/`to`/`alongOrientationOf` object references.
