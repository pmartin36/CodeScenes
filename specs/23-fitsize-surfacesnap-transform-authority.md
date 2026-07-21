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
  back** on the next evaluate — **provided the drag is within a capture threshold**. Dragged **far**
  (beyond the threshold), it **detaches stickily**: the snap turns OFF (as if disabled) and stays off
  until the author re-enables / re-authors `.SurfaceSnap(...)` — it does **not** re-snap merely by
  dragging back into range. Free (unset) axes move normally. The transform position is real and synced.

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
  keep the scene transform in sync. Reconcile reads the transform normally (it's real); the code EMIT
  omits an intent-owned channel per Resolved-decision #1 (emit `.FitSize`/`.SurfaceSnap`, not the
  redundant `.Transform(...)`).

## Gate coverage (close the gap that let the bug escape)
- Add EditMode tests that drive the **real `SceneBuilderBuild.Run` + component-drive** path (not a
  hand-positioned direct `Evaluate()`): author `.FitSize(height:1.2f).SurfaceSnap(down:true)` on a Crate
  above a floor, Build, and assert the **observed** geometry (Crate ≈1.2 tall, its bottom flush on the
  floor) — the exact path the write-skip broke. Also cover: manual scale → FitSize back-solves; drag
  within threshold → snaps back; drag beyond threshold → detaches.

## Resolved decisions (ratified by the user)

1. **Source round-trip = intent owns the channel.** The **scene** transform is always written on
   materialize and is fully real (this is the v1 bug fix). But in the **code emit** (scene→code), a
   channel a FitSize/SurfaceSnap owns is emitted as the **intent** — `.FitSize(height:)` /
   `.SurfaceSnap(down:)` — and **NOT** redundantly as `.Transform(scale:)`/`(pos:)` on that channel. If
   an LLM authors both `.Transform(scale:2)` and `.FitSize(height:1.2)`, **intent wins**: the component
   drives the scene, and the redundant `.Transform(scale:2)` is **reconciled away** on the next
   round-trip (it is not re-emitted). So there is a targeted suppression, but **only in the code-emit
   direction** — never a scene-write skip (that was v1's mistake).
2. **SurfaceSnap detach = sticky.** Past the capture threshold the snap turns **OFF** until re-authored /
   re-enabled; it does not re-snap on drag-back. (Threshold is a configurable capture distance in world
   units; pick a sensible default at build, e.g. ~2–3 units, and expose it on the component.)
3. **DrivenChannels / ChannelMask = repurposed, not deleted.** It now marks **which transform channel
   the CODE EMIT omits** (because an intent component owns it), NOT a scene-write suppression. Materialize
   always writes the full transform; only the reconcile/emit consults it to prefer the intent form.

## Dependencies
- Revises **spec 19** (FitSize/SurfaceSnap). Removes its DrivenChannels transform-suppression.
- Reuses **M3** (components/fields), **M1/M2** (transform materialize/reconcile), the ComponentReconciler
  emit path, and the build-strip (`IProcessSceneWithReport`) which stays as-is.
