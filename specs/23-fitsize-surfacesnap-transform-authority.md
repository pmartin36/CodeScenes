# FitSize / SurfaceSnap v2 — transform-first authority (revises spec 19)

**Status:** spec for a future build. Revises `specs/19-spatial-authoring-components.md`. The core change:
**the transform is a first-class, always-authored, always-synced channel — it is NEVER suppressed.**
FitSize/SurfaceSnap write INTO the transform and stay in sync with it, rather than hiding it.

## Why (the mistake in v1)

Spec 19 shipped a **DrivenChannels suppression** model: the components drive scale/position, and those
channels are suppressed from the transform — not written on materialize, not synced back. That is wrong:

- **The transform is THE interface.** People drag objects in the scene (transform); LLMs write
  `pos:`/`scale:` in code (transform). We cannot assume exclusive component control or that an LLM will
  set FitSize *without* also setting the transform.
- **It caused a real bug (confirmed).** The materialize **write-skip** (PlanExecutor per-axis skip of
  driven channels) zeroed a Crate's authored `y=3` → the Crate built at `(3,0,0)`, embedded in the
  floor → `SurfaceSnap(down)` found no surface *below* its bottom face → never snapped (crate bisects
  the floor). The gate missed it because the v1 EditMode tests call `Evaluate()` **directly on
  hand-positioned objects** and never go through the real `SceneBuilderBuild.Run` + live-drive path.
- Cosmetics: FitSize's NaN-sentinel serialized floats look broken in the Inspector; SurfaceSnap's 6
  independent bools allow contradictory states (up AND down).

## New authority model

**The transform (position/scale/rotation) is written on materialize in full and synced normally. There
is no transform suppression.** FitSize/SurfaceSnap are live editor-time components that write their
result INTO the transform and keep it consistent:

### FitSize ↔ transform: two-way, always matching
- FitSize drives `localScale` to hit the authored world-size intent (height/width/depth/size).
- **When the user manually scales the object, FitSize back-solves its intent to match** — so FitSize's
  value and the transform never disagree (scale the object up → `FitSize.height` updates to the new
  world height). The transform scale is real and synced; the intent round-trips too; they stay in sync.

### SurfaceSnap: constraint has precedence, with a detach threshold
- SurfaceSnap drives position on its snapped axes to keep the object's world-bounds face flush to the
  surface. **The constraint wins:** if the user drags the object away on a snapped axis, it **snaps
  back** on the next evaluate — **provided the distance to the surface is within a threshold**. Dragged
  **far** (beyond the threshold), it **detaches** (releases/stops snapping) so the object can be moved
  off deliberately. Free (unset) axes move normally. The transform position is real and synced.

## Data model (enums — no NaN, no contradictory bools)

- **SurfaceSnap:** replace the 6 bools with **three per-axis enums** — `Vertical {None,Up,Down}`,
  `Horizontal {None,Left,Right}`, `Depth {None,Forward,Back}`. Contradictory states become impossible;
  Unity renders enums as dropdowns by default.
- **FitSize:** replace the NaN-sentinel floats with a **mode enum** `{Width,Height,Depth,Explicit}` + a
  `value` float + a `size` Vec3. No NaN; the driving axis is explicit.
- **Authoring API unchanged:** `.FitSize(height: 1.2f)`, `.SurfaceSnap(down: true)`. Parse/emit/materialize
  map to the new fields; update the `SpatialComponents` type/field-name constants accordingly.

## Custom inspectors
- **FitSize:** mode dropdown + only the relevant value field shown (hide the unused one).
- **SurfaceSnap:** three per-axis dropdowns (default enum rendering, plus a small threshold/detach field).

## The write-skip fix (the actual bug)
- **Remove** the materialize DrivenChannels write-skip (`PlanExecutor.ApplyTransformField` per-axis skip)
  and the Diff/Materializer transform-channel suppression. Materialize writes the **authored transform in
  full** (including channels the components will then drive). The components drive from that start and
  keep the transform in sync. Reconcile syncs the transform normally too (no suppression).

## Gate coverage (close the gap that let the bug escape)
- Add EditMode tests that drive the **real `SceneBuilderBuild.Run` + component-drive** path (not a
  hand-positioned direct `Evaluate()`): author `.FitSize(height:1.2f).SurfaceSnap(down:true)` on a Crate
  above a floor, Build, and assert the **observed** geometry (Crate ≈1.2 tall, its bottom flush on the
  floor) — the exact path the write-skip broke. Also cover: manual scale → FitSize back-solves; drag
  within threshold → snaps back; drag beyond threshold → detaches.

## Open questions (resolve at build time)
1. **Round-trip when both transform and FitSize/SurfaceSnap are present.** The transform now syncs, so a
   FitSize-driven scale would also appear as `.Transform(scale:)` in source alongside `.FitSize(height:)`.
   Decide: redundant-but-consistent (both in source, kept in sync), or FitSize/SurfaceSnap "own" their
   channel in source and the transform omits it (a lighter, targeted suppression only in the
   code-emit direction, not the scene). This is the crux the v1 suppression got wrong — get it right here.
2. **SurfaceSnap detach threshold:** the exact distance, per-axis vs world, and whether detach is sticky
   (stays off until re-authored) or re-attaches when dragged back within range.
3. **DrivenChannels/ChannelMask:** whether the v1 mechanism is deleted outright or repurposed for the
   narrow code-emit case in (1).

## Dependencies
- Revises **spec 19** (FitSize/SurfaceSnap). Removes its DrivenChannels transform-suppression.
- Reuses **M3** (components/fields), **M1/M2** (transform materialize/reconcile), the ComponentReconciler
  emit path, and the build-strip (`IProcessSceneWithReport`) which stays as-is.
