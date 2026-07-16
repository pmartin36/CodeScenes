# M-Spatial — Spatial authoring components (Sizer + Snapper, editor-time live constraints)

### Additions to the contract

**Two editor-only components + one suppression concept — no new `ValueNode` kind.** This milestone adds
two live-evaluating **editor-time components** (`Sizer`, `Snapper`) that let an author express spatial
**intent** while the tool computes the geometry, and one Core concept — **driven transform channels** —
that suppresses the transform channels those components derive from both directions of sync. The
components **are** M3 components: they are authored, materialized and reconciled through the shipped
`ComponentHandle` / `ComponentReconciler` / `ComponentPatchApplier` machinery, and their fields are the
existing `ValueNode` value kinds (float, bool, `Vec3`, `ObjectRef`). No new `ValueNode`, no new
`PlanOp`, no new sidecar collection.

Why this shape: an LLM cannot reason about space — it does not know a `Character.fbx` is 180 native
units tall or where a floor is — but it can state intent ("2 metres tall", "resting on the floor",
"in the bottom-left corner"). Making that intent a **persistent, live-re-evaluating** component (not a
one-time bake) is what keeps the geometry correct as the scene moves, and modelling it as an M3
component means the whole author↔scene round-trip is a proven path, not new machinery.

| Added | Shape (summary) | Owner |
|---|---|---|
| `Sizer` (runtime-assembly `[ExecuteAlways]` MonoBehaviour) | drives **local scale** so the mesh hits an author-declared **world-space** size; stripped from player builds | M-Spatial |
| `Snapper` (runtime-assembly `[ExecuteAlways]` MonoBehaviour) | drives **local position** on chosen axes so the mesh's real bounds rest against a surface; stripped from player builds | M-Spatial |
| `TransformData.DrivenChannels : ChannelMask` | the set of local transform channels **derived** by an editor-only component, **suppressed** from transform diff/emit in **both** directions; a first-class generalization of §13's RectTransform X/Y double-authority rule | M-Spatial |
| `.Sizer(...)` / `.Snapper(...)` authoring sugar + dedicated parse/emit arms | fluent node-handle methods (siblings of `.Transform(…)`), lowering to an M3 component-with-fields | M-Spatial |

`DrivenChannels` is **re-derived on each side, never persisted** (the sidecar stays untouched, exactly
as §17 re-derives a built-in name live) — the expected model derives it from the `.Sizer/.Snapper`
call at parse; the snapshot derives it from the live components at read. Both sides therefore always
agree, and no `SchemaVersion` bump is required.

> **Milestone position.** This is ordered **after M-Auto (`specs/14`)** — it assumes the seamless,
> non-user-driven sync loop is the delivery vehicle (the components re-evaluate continuously, so the
> product only makes sense under auto-sync) — and **before the real M5 cross-object refs
> (`specs/06`)**. The one place this milestone brushes M5 (Snapper's optional explicit-target
> override) is flagged as a dependency, not built ahead of it (§Dependencies, §Risks).

---

## Goal

An author (human or LLM) assigns any mesh a **world-space size** and **snaps it to a surface** without
knowing the mesh's native dimensions or where the surface is:

```csharp
scene.Add("Crate")
     .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Cube")))     // §17
     .Sizer(height: 1.2f)                                              // 1.2 world units tall, aspect kept
     .Snapper(down: true);                                            // its real bottom rests on the floor
```

`Builtin("Cube")` happens to be 1×1×1, but `Character.fbx` is not — the point is the author never needs
to know. After this milestone: `Build` assigns the components, the components compute and apply the
scale/position **live** at edit time, moving the floor makes the crate follow, and the derived scale
and position are **never written back into source** — only the intent (`height: 1.2f`, `down: true`)
lives in code.

## The space problem (why this exists)

An LLM emits code by pattern, not by measuring geometry. Three failures recur and none are fixable by
better prompting:

1. **Native size is unknown and arbitrary.** A mesh authored in metres, centimetres or "Maya units"
   imports at a scale the model has never seen. `transform.localScale = (1,1,1)` yields a building or a
   speck with equal confidence. The fix is to specify the *result* (world size) and let the tool read
   the mesh bounds and solve for the scale.
2. **Placement is relational, not absolute.** "On the floor", "against the back wall", "in the
   bottom-left corner" are surface-relative. The floor's Y, and the object's own real extent, are both
   unknown to the model at authoring time; a literal `pos:(x,y,z)` is a guess that breaks the moment
   either moves.
3. **Pivots lie.** A character mesh may be pivoted at its feet, its centre, or its head. `position.y =
   floorY` lands the *pivot* on the floor — sinking or floating the character depending on where the
   pivot happens to be. Correct placement must use the object's **real world bounds**, not its origin.

Sizer solves (1); Snapper solves (2) and (3). Both must be **live** (constraint 2/3 have moving inputs)
and both must keep source **intent-only** (the product is continuous invisible sync — a derived scale
or position churned into source on every floor-nudge is fatal).

## In scope

- **`Sizer`** — declare a mesh's target **world-space** size, indifferent to native size. Two authoring
  forms:
  - **Aspect-locked fit (default):** exactly one of `width:` / `height:` / `depth:` drives; the other
    two axes scale proportionally so the mesh's shape is preserved. This is the LLM-natural form
    ("make it 2 m tall").
  - **Explicit per-axis:** `size: (x, y, z)` pins all three world-space dimensions independently
    (non-uniform allowed).
  Size is interpreted as **local mesh bounds size × world scale**, axis-aligned in the object's local
  space — i.e. **rotation-independent** (rotating the object does not change what "2 m tall" means; see
  §"Editor adapter deliverables" for why `Mesh.bounds`, not `Renderer.bounds`, is read).
- **`Snapper`** — snap a mesh to a surface, **origin-agnostic**: uses the mesh's actual **world
  bounds**, never its pivot. The author picks **one horizontal** (`left` / `right` / none) **and one
  vertical** (`up` / `down` / none); the two combine (bottom-left corner = `down:true, left:true`; onto
  a ceiling = `up:true`; onto a floor = `down:true`). A character pivoted at feet, centre or head all
  land identically.
- **Snap target = raycast scene geometry, no wiring** (default), **with an optional explicit target
  override.** Cast from the chosen bounds face along the chosen direction; land on the first surface
  hit. Requires colliders on candidate objects, or falls back to their renderer/mesh bounds
  (§"Editor adapter deliverables"). The author may pass an explicit target node handle to bypass the
  raycast.
- **Snapper runs AFTER Sizer.** It needs the final, post-sizing world size to know where the object's
  real face is. This ordering is enforced at evaluate time (`[DefaultExecutionOrder]`) and preserved in
  emitted source (Sizer call before Snapper call).
- **Live re-evaluation, NOT bake-once.** Both components persist as editor-time components and
  re-evaluate when the mesh, its ancestors, or the surface move (move the floor → the snapped object
  follows). Achieved by `[ExecuteAlways]`; no user button.
- **Driven-channel suppression (both directions).** The transform channels the components derive —
  Sizer → local scale; Snapper → local position on the snapped axes — are **driven**: code holds the
  intent (`.Sizer(...)` / `.Snapper(...)`) and **never** the resulting scale/position; Materialize
  never emits a `SetTransform`/`SetField` for a driven channel, and Reconcile never patches a
  `.Transform(scale:/pos:)` argument for one. Reuses §13's double-authority model, generalized.
- **Build-strip.** The components are editor-time-only; they are removed from real player builds, the
  baked transform values remaining.
- **Both-direction round-trip of intent.** Author `.Sizer/.Snapper` in code → components applied in
  scene; edit a component's field in the Inspector → the dedicated call's argument is patched back;
  a component added to an editor-created object → the dedicated call is appended (§13); ordering
  survives both directions.

## Out of scope

- **Physics simulation / runtime behavior.** These are editor-time constraints, not `Rigidbody`
  settling or gameplay. They no-op at runtime and are stripped from builds.
- **Collision resolution between multiple snapped objects.** Each object snaps to scene surfaces
  independently; two Snapper objects are not de-conflicted against each other (no mutual pushout, no
  stacking solver).
- **Non-mesh objects.** A GameObject with no `MeshFilter`+mesh / no `Renderer` (empties, lights,
  cameras, bare `RectTransform` UI) has no bounds to read → out of scope; `.Sizer/.Snapper` on such a
  node is a located error, not a guess. (UI layout is §13's job.)
- **Rotation authoring / surface-normal alignment.** Neither component drives rotation, and Snapper
  snaps only along the world axes (up/down/left/right) — it does not orient the object to a slanted
  surface's normal. Snapping along **depth (world Z)** is intentionally **not** offered: the
  horizontal/vertical model leaves Z free for the author's `.Transform(pos:)` (see §"Authoring API",
  flagged OPEN).
- **Precise fitting to concave colliders / mesh silhouettes.** Bounds are axis-aligned boxes; a
  concave mesh snapped into a corner rests by its AABB, not its true hull.
- **Composing a whole primitive / mesh import** — orthogonal (§17 authors the mesh ref; M1 the transform).

## Core deliverables

Nothing here touches Unity — Core is handed already-parsed intent and a snapshot in which the adapter
has already marked driven channels. New file `SceneBuilder.Core/Model/ChannelMask.cs` plus additions to
existing parse/emit/diff/reconcile files.

**Types added/changed**

- **`ChannelMask` (new `[Flags]` enum)** — `None, PositionX, PositionY, PositionZ, ScaleX, ScaleY,
  ScaleZ`. (Rotation is never driven by these components.) Convenience: `Scale = ScaleX|ScaleY|ScaleZ`.
- **`TransformData.DrivenChannels : ChannelMask`** — `None` by default, so every existing node is
  unchanged. Populated **only** from editor-only-component presence (never authored directly, never
  serialized to the sidecar). Derivation:
  - a node carrying a `Sizer` ⇒ `|= Scale`;
  - a node carrying a `Snapper` with `left`/`right` set ⇒ `|= PositionX`;
  - a node carrying a `Snapper` with `up`/`down` set ⇒ `|= PositionY`.
- **`Sizer` / `Snapper` as M3 component data.** In the `SceneModel` these are ordinary components in the
  node's component list (a type ref + a field map), identical in shape to any `.Component<T>` — the
  fields are `ValueNode.Bool`/float/`ValueNode.Vec3`/`ValueNode.AssetRef`(target). **No new model type.**
  The Sizer field map is one of `{ width|height|depth : float }` (aspect-locked) or `{ size : Vec3 }`
  (explicit); the Snapper field map is `{ up, down, left, right : bool, target : ObjectRef? }`.

**Functions/behaviors (each a testable contract)**

- **Parse `.Sizer(...)` / `.Snapper(...)`** — new arms in `BuilderParser`, matched exactly like the
  existing `.Transform(...)` / `.Component<T>(...)` node-handle calls, producing the corresponding
  component-with-fields **and** stamping `TransformData.DrivenChannels`. The parser stays **total**:
  - `.Sizer(height: 2f)` → Sizer component `{ height: 2 }`, `DrivenChannels |= Scale`.
  - `.Sizer(size: (2,1,0.5f))` → Sizer component `{ size: (2,1,0.5) }`, `DrivenChannels |= Scale`.
  - `.Sizer(height: 2f, size: (…))` (aspect **and** explicit together) → **located error** (§7):
    ambiguous size authority, never a silent pick.
  - `.Snapper(down: true, left: true)` → Snapper `{ down:true, left:true }`, `DrivenChannels |=
    PositionX|PositionY`.
  - `.Snapper(left: true, right: true)` (or `up`+`down`) → **located error**: contradictory axis.
  - `.Snapper(down: true, target: floor)` → additionally an `ObjectRef` to the `floor` handle's
    LogicalId (see §Dependencies for the resolution path).
  - malformed/empty/non-literal args → `Unsupported(raw)` (never throw), matching the `Asset`/`Builtin`
    parser contract.
- **Driven-channel suppression in `Diff` (both directions), the load-bearing behavior.** For a node
  whose `DrivenChannels` includes a channel, `Diff` emits **no** `SetTransform`/`SetField` op for that
  channel — regardless of how the live snapshot's scale/position differs from anything in code. Code
  does not carry those values, so there is nothing to diff. Concretely:
  - Sizer node: a snapshot `m_LocalScale` differing from the model produces **no** scale op.
  - Snapper-down node: a snapshot `m_LocalPosition.y` drift produces **no** position-Y op; `.x`/`.z`
    still diff normally (the free axes).
  - The **component fields themselves** (Sizer `height`, Snapper flags/target) diff **normally** via M3
    — those are the authored intent and must round-trip.
  This is exactly §13's "X/Y localPosition drift on a RectTransform yields no `SetTransform`" rule,
  lifted from a RectTransform special case to a general `DrivenChannels` gate. **§13's version is not
  built** (§Dependencies) — this milestone specifies the general mechanism and §13 can later fold into it.
- **Emit `.Sizer(...)` / `.Snapper(...)`** — dedicated `SourceExpr`/emit arms producing the fluent form
  (**not** the generic `.Component<Sizer>(c => c.Set(...))`), so scene→code reads as authored:
  - Sizer → `.Sizer(height: 2f)` / `.Sizer(size: (2f, 1f, 0.5f))` (floats f-suffixed via the shared
    `SourceExpr.Float`/`Vec3Literal`, invariant culture, exactly as §13).
  - Snapper → `.Snapper(down: true, left: true)` (only the set flags emitted) / `.Snapper(down: true,
    target: floor)`.
- **Deterministic ordering: Sizer before Snapper.** When both are emitted onto one node, the Sizer call
  precedes the Snapper call in source, mirroring the evaluate-time execution order; the ordering is
  stable across re-emits (no churn).
- **Reconcile (scene→code).** Reuses `ComponentReconciler` for the component fields, formatted through
  the dedicated emit:
  - an Inspector edit to a Sizer/Snapper field → a `PatchArgument` rewriting only that argument of the
    `.Sizer(...)` / `.Snapper(...)` call (formatting preserved, §5);
  - a Sizer/Snapper added to an editor-created object → the dedicated call appended onto that node's
    `.Add(...)` statement in one pass (§13 create-with-payload), second Sync a no-op;
  - **driven scale/position never emitted:** because those channels are suppressed in Diff, a live
    scale/position change (whether from the component's own drive or a user drag) produces **no**
    `.Transform(scale:/pos:)` patch.
- **Idempotent.** Materialize → parse → re-materialize yields no ops; Reconcile → apply → re-reconcile
  yields no edits; the second Sync of an unchanged scene is a no-op.

## Editor adapter deliverables

The geometry lives in the **runtime** assembly (`com.codescenes/Runtime`, `SceneBuilder.Authoring` —
`autoReferenced`, no `includePlatforms`, so it ships to players); the build-strip and the SceneBuilder
sync wiring live in the **Editor** assembly (`com.codescenes/Editor`, `SceneBuilder.Editor`,
`includePlatforms:["Editor"]`). The runtime component's geometry uses **only runtime APIs** (no
`UnityEditor` reference, so no `#if UNITY_EDITOR` guard is needed around the math).

- **`Sizer : MonoBehaviour` (runtime, `[ExecuteAlways]`, `[DefaultExecutionOrder(-100)]`).**
  Serialized fields backing the authored intent (`float width/height/depth` with a "driving axis"
  discriminator, or `Vector3 size` + an `explicit` flag). Each editor frame (guarded
  `if (Application.isPlaying) return;`), and on `OnValidate`:
  - read the sibling `MeshFilter.sharedMesh.bounds` — the **local** AABB (`Mesh.bounds`), because scale
    is a **rotation-independent** property; `Renderer.bounds` is the *rotated world* AABB and would make
    "2 m tall" change as the object turns.
  - solve `localScale` so that `localBoundsSize · lossyScale == targetWorldSize`, dividing out the
    parent `lossyScale` (`transform.parent.lossyScale`) so the result is correct under a scaled parent.
    Aspect-locked: the one driven axis sets a uniform factor applied to all three; explicit: per-axis.
  - write `transform.localScale`. (A zero/degenerate bounds extent → no-op + one located warning, never
    a divide-by-zero.)
- **`Snapper : MonoBehaviour` (runtime, `[ExecuteAlways]`, `[DefaultExecutionOrder(-90)]` — after
  Sizer).** Serialized `bool up/down/left/right` and an optional `Transform target`. Each editor frame /
  `OnValidate` (same runtime guard):
  - read the sibling `Renderer.bounds` — the **world** AABB (`Renderer.bounds`), because snapping needs
    the object's *actual world footprint* including rotation and pivot; this is what makes it
    **origin-agnostic** (the real bottom face, not `transform.position`).
  - for each snap direction, cast from that bounds face toward the surface. Default path:
    `Physics.Raycast` from a small grid of points across the face (centre + corners, to survive uneven
    surfaces), take the first/nearest hit; **requires colliders**. Fallback when no collider is hit:
    scan candidate scene `Renderer`s (or `MeshFilter` bounds) along the cast axis and take the nearest
    opposing bounds face. **Override:** if `target` is set, skip the raycast and use that object's
    `Renderer.bounds` face directly.
  - compute the position delta that brings the chosen bounds face flush to the hit surface and apply it
    to `transform.position` (only the snapped world axes; other axes untouched). Because it works from
    `Renderer.bounds`, the pivot location is irrelevant.
  - re-evaluation is automatic: `[ExecuteAlways]` re-runs each editor frame, so moving the floor or the
    object updates the snap. (Perf: gate the recompute on `transform.hasChanged` / a surface-dirty
    check to avoid a raycast per idle frame — §Risks.)
- **Driven-channel wiring into SceneBuilder sync.**
  - **Read (snapshot):** when the snapshot reader finds a live `Sizer`/`Snapper` on an object, it stamps
    the node's `TransformData.DrivenChannels` (the symmetric side of Core's parse-time derivation) so
    Diff suppresses those channels for that object.
  - **Write (materialize):** the transform writer skips any channel in `DrivenChannels` — it never
    fights the component. The **components themselves** are materialized as ordinary M3 components
    (`ComponentHandle`/serialized-field write), and *they* drive the transform.
- **Build-strip via `IProcessSceneWithReport` (Editor assembly).** `OnProcessScene(Scene, BuildReport
  report)` runs on the build's scene copy: when `report != null` (a real player build), for every
  `Sizer`/`Snapper` in the scene, force one final `Evaluate()` (guarantees the baked transform is
  current) then `Object.DestroyImmediate(component)`. The player build ships the **baked transform**
  and **no** SceneBuilder component — hence no "missing script". (In editor play mode `report` is null:
  the component is not stripped but no-ops via its `Application.isPlaying` guard, so it is harmless.)
- **Inspector "driven" affordance (enhancement, flagged).** To make "the constraint wins" visible,
  grey the driven scale/position fields in the Transform inspector. RectTransform has the public
  `DrivenRectTransformTracker`; a plain Transform's scale/position uses the **internal**
  `DrivenPropertyManager` (reflection). This is cosmetic — the real enforcement is `[ExecuteAlways]`
  reverting manual edits — so it is a nice-to-have, not a gate (see §Risks / OPEN).
- **EditMode coverage** (`unity-gate/Assets/GateTests/`, per CLAUDE.md) — §"Unity confirmation checklist".

## Authoring API

Inert fluent stubs on `NodeHandle` (`com.codescenes/Runtime/NodeHandle.cs`), siblings of the existing
`.Transform(…)` / `.Component<T>(…)` — compile-time scaffolding that SceneBuilder parses from source and
never executes (identical treatment to `.Transform`, which is already an inert `=> this` stub).

```csharp
// Sizer — aspect-locked fit (the default form: exactly one driving dimension, aspect preserved)
public NodeHandle Sizer(float? width = null, float? height = null, float? depth = null);
// Sizer — explicit per-axis world size (non-uniform allowed)
public NodeHandle Sizer(Vector3 size);

// Snapper — one horizontal (left|right), one vertical (up|down), optional explicit target override
public NodeHandle Snapper(bool up = false, bool down = false,
                          bool left = false, bool right = false,
                          NodeHandle target = null);
```

Rules (enforced by the parser, §"Core deliverables"): Sizer takes **exactly one** of
`width`/`height`/`depth` (aspect-locked) **or** `size:` (explicit) — never both, never none. Snapper
takes **at most one** of `{up,down}` and **at most one** of `{left,right}`; contradictory pairs are a
located error.

```csharp
using static SceneBuilder.Authoring.AssetRefs;

public class ArenaScene : ISceneDefinition {
    public void Build(SceneRoot scene) {
        var floor = scene.Add("Floor")
            .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Plane")))
            .Sizer(size: (20, 1, 20));                       // 20×20 world units

        // A crate: 1.2 m tall (aspect kept), resting on whatever floor is below it.
        scene.Add("Crate")
             .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Cube")))
             .Sizer(height: 1.2f)                            // Sizer first…
             .Snapper(down: true);                           // …Snapper second (needs the final size)

        // A poster in the back-left corner of the room, snapped to two surfaces at once.
        scene.Add("Poster")
             .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Quad")))
             .Sizer(width: 0.8f)
             .Snapper(down: true, left: true);

        // A light fixture snapped up onto the ceiling — explicit target, no raycast needed.
        var ceiling = scene.Add("Ceiling").Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Plane")));
        scene.Add("Lamp")
             .Component<MeshFilter>(c => c.Set("m_Mesh", Builtin("Cylinder")))
             .Sizer(height: 0.3f)
             .Snapper(up: true, target: ceiling);
    }
}
```

Lowering (Editor→Core): `.Sizer(...)` / `.Snapper(...)` → an M3 component-with-fields on the node **and**
`TransformData.DrivenChannels` set; base `.Transform(rot:/pos:)` may still be chained for the
non-driven channels (e.g. rotation, or a free axis Snapper doesn't touch).

## Core test plan

`SceneBuilder.Core.Tests` (xUnit, headless, style `Subject_Condition_ExpectedOutcome`). New file
`SpatialComponentTests.cs`, plus additions to `DiffTests`/reconcile suites. The geometry is **not**
tested here (it is Unity-only, §"Unity confirmation checklist"); Core tests cover parse, emit,
driven-suppression, and round-trip of *intent*.

**Parse + driven derivation**
1. `Parse_SizerHeight_YieldsSizerComponentAndDrivenScale` — `.Sizer(height: 2f)` → Sizer `{height:2}`,
   `DrivenChannels == Scale`.
2. `Parse_SizerExplicitSize_YieldsPerAxisFieldsAndDrivenScale` — `.Sizer(size:(2,1,0.5f))`.
3. `Parse_SizerAspectAndExplicitTogether_YieldsLocatedError` — both forms → located error, no throw.
4. `Parse_SizerNoDimension_YieldsLocatedError`.
5. `Parse_SnapperDownLeft_SetsFlagsAndDrivenPositionXY` — `DrivenChannels == PositionX|PositionY`.
6. `Parse_SnapperDownOnly_DrivesPositionYNotX` — `DrivenChannels == PositionY`.
7. `Parse_SnapperContradictoryAxis_YieldsLocatedError` — `left:true,right:true` (and `up:true,down:true`).
8. `Parse_SnapperWithTarget_CarriesObjectRefToHandleLogicalId`.
9. `Parse_NodeWithoutSpatialComponent_DrivenChannelsNone`.

**Diff / suppression** (the load-bearing pins)
10. `Diff_SizerNode_ScaleDriftInSnapshot_ProducesNoScaleOp`.
11. `Diff_SnapperDownNode_PositionYDrift_ProducesNoPositionYOp_ButXZStillDiff`.
12. `Diff_SizerFieldChanged_ProducesComponentFieldChange` — intent still diffs via M3.
13. `Diff_SnapperTargetRewired_ProducesComponentFieldChange`.
14. `Diff_NonDrivenNode_ScaleAndPositionStillDiffNormally` — regression: suppression is opt-in per channel.

**Emit + ordering**
15. `Emit_Sizer_EmitsDedicatedCallNotGenericComponent` — `.Sizer(height: 2f)`, not `.Component<Sizer>`.
16. `Emit_SizerExplicit_EmitsSizeVectorFSuffixed`.
17. `Emit_Snapper_EmitsOnlySetFlags` — `.Snapper(down: true, left: true)`.
18. `Emit_SizerBeforeSnapper_OrderingDeterministicAndStable` — both present → Sizer precedes Snapper;
    re-emit identical (no churn).
19. `Parse_Emit_SpatialCalls_TextRoundTripsIdentically`.

**Reconcile**
20. `Reconcile_EditedSizerHeightInScene_PatchesSizerArgumentOnly`.
21. `Reconcile_DrivenScaleAndPosition_NotEmittedToSource` — a live scale/position change produces **no**
    `.Transform(...)` patch (suppression through reconcile).
22. `Reconcile_CreatedNodeWithSnapper_AppendsSnapperCall_SecondSyncNoOp` — §13 create-with-payload.
23. `Reconcile_SnapperAndSizerOnCreatedNode_AppendsInSizerThenSnapperOrder`.
24. `RoundTrip_SizerSnapper_Idempotent` — Materialize→parse→re-materialize and Reconcile→apply→
    re-reconcile both no-op.

## Unity confirmation checklist

EditMode round-trips in `unity-gate/Assets/GateTests/` (new `RoundTripSpatialTests.cs`, style
`Direction_Scenario_Expectation`), driving `SceneBuilderBuild.Run` / the Sync path against a live scene
with real meshes, colliders, `Renderer.bounds` and `Physics.Raycast`. Assertions are on **observed
geometry**, not labels.

1. **Sizer hits an exact world size.** Author `.Sizer(height: 2f)` on a non-unit mesh; Build.
   *Expected:* the object's `Renderer.bounds.size.y ≈ 2` (tolerance ~1e-3), width/depth scaled by the
   same factor (aspect preserved).
2. **Sizer under a scaled parent.** Parent `lossyScale = 3`; `.Sizer(height: 2f)` on the child.
   *Expected:* child still `bounds.size.y ≈ 2` world units (parent scale divided out).
3. **Sizer explicit per-axis.** `.Sizer(size: (2,1,0.5f))` on a Cube. *Expected:* bounds `≈ (2,1,0.5)`.
4. **Snapper down, three pivots.** Take the same mesh authored with pivot at feet, centre, and head;
   `.Snapper(down: true)` over a floor at `y = 0`. *Expected:* all three land with
   `Renderer.bounds.min.y ≈ floorTop` (origin-agnostic — the headline pivot test).
5. **Snapper up onto a ceiling.** `.Snapper(up: true)` under a ceiling. *Expected:* `bounds.max.y ≈
   ceilingBottom`.
6. **Snapper corner.** `.Snapper(down: true, left: true)` in a corner. *Expected:* `bounds.min.y ≈
   floorTop` **and** `bounds.min.x ≈ leftWallInner`.
7. **Sizer + Snapper together, ordering.** `.Sizer(height: 1.2f).Snapper(down: true)`. *Expected:* the
   object is 1.2 units tall **and** its (post-resize) bottom rests on the floor — proving Snapper read
   the final size, not the pre-Sizer size.
8. **Move the floor → snapped object follows.** After #4, raise the floor by 1 unit; let the editor
   tick. *Expected:* the object's bottom tracks to the new floor top (live re-evaluation, no Build).
9. **Raycast fallback (no collider).** Snap onto a floor object that has a `Renderer` but **no
   Collider**. *Expected:* the fallback resolves the surface from renderer bounds and the object still
   lands.
10. **Explicit target override.** `.Snapper(up: true, target: ceiling)` where the raycast up would
    otherwise hit a different object first. *Expected:* it snaps to `ceiling`, not the raycast hit.
11. **Driven channels not emitted (the anti-churn pin).** Author `.Sizer(height: 2f).Snapper(down:
    true)`; Build; then Sync with no edit. *Expected:* source is unchanged — it contains `.Sizer(...)`
    and `.Snapper(...)` but **no** `.Transform(scale: …)` / `.Transform(pos: …)`; the Sync is a no-op.
12. **Manual drag reverts, no source write (constraint wins).** Hand-drag the snapped object in the
    scene view; let the editor tick, then Sync. *Expected:* the component re-snaps it on the next frame
    **and** the Sync produces no source change (driven position suppressed). (Ratify — §Risks.)
13. **Field edit round-trips.** Change the `Sizer` height in the Inspector, or toggle a `Snapper` flag;
    Sync. *Expected:* the `.Sizer(height: …)` / `.Snapper(...)` argument updates; nothing else.
14. **Created-in-editor object with a Snapper.** Add a GameObject in the editor, add a `Snapper`
    component, set `down`; Sync. *Expected:* a `.Add("…")…​.Snapper(down: true)` statement appears
    (§13), compiles, and a second Sync is a no-op.
15. **Build-strip.** Build a player scene (or invoke the `IProcessSceneWithReport` on a scene copy with
    a non-null report). *Expected:* the built GameObject has **no** `Sizer`/`Snapper` component and **no
    missing script**, and its transform retains the baked size/position.
16. **Non-mesh node is refused.** `.Sizer(height: 2f)` on an empty GameObject (no `MeshFilter`).
    *Expected:* a located error naming the node — not a silent no-op or a divide-by-zero.

## Dependencies

- **M1** (`specs/completed/02-*`) — `TransformData`, `Vec3`, `SetTransform`/`SetField`, the
  `IdentityMap` `LogicalId↔GlobalObjectId`, and the `.Transform(…)` authoring pattern these mirror.
  `DrivenChannels` is added to `TransformData`; the transform writer/reader are the suppression seam.
- **M3** (`specs/completed/04-m3-components-fields.md`) — **verified present**: `.Component<T>(c =>
  c.Set(...))` (`com.codescenes/Runtime/ComponentHandle.cs`, `NodeHandle.cs`), and scene→code field
  reconcile (`SceneBuilder.Core/Reconcile/ComponentReconciler.cs` + `ComponentPatchApplier.cs`). Sizer
  and Snapper **are** M3 components — authored, materialized and reconciled through this machinery; only
  their *dedicated source form* and *driven-channel* behavior are new.
- **M2 / M2b** — Reconcile, `PatchArgument`, formatting-preserving apply, `AppendStatement`, the shared
  `SourceExpr` float/`Vec3Literal` emitter, and §13 create-with-payload single-pass convergence.
- **M4** (`specs/completed/05-m4-asset-references.md`) / **§17** — Sizer reads the node's mesh, assigned
  as an `AssetRef` (`Asset(...)` or `Builtin(...)`); no code dependency, but a mesh must be present.
- **§13 (`specs/13-recttransform.md`) — conceptual origin, NOT a build dependency.** §13 defines the
  driven/suppressed-transform-channel idea (RectTransform X/Y double-authority). **Verified NOT
  implemented:** `SceneBuilder.Core` contains **no** RectTransform/`AnchoredPosition`/`Driven`/
  `SetRectTransform` code today (§13 is not in `completed/`). Therefore this milestone **does not depend
  on §13 having shipped**; it **specifies its own** general `DrivenChannels` mechanism, which §13's
  RectTransform case can later be re-expressed in terms of. Cite §13 for the model, own the mechanism.
- **M5 (`specs/06-m5-cross-object-references.md`) — the Snapper `target:` override rides on M5's
  object-reference resolution.** The raycast path (the headline, no wiring) needs nothing from M5. The
  explicit target is a handle → `LogicalId` → live object; same-run/already-declared targets resolve via
  the existing `IdentityMap` today, but a **forward-referenced** target (declared later in the file)
  needs M5's two-pass Materialize. Since this milestone is ordered **before** M5, the target override is
  specified but **flagged** (§Risks): ship it constrained to already-declared handles now, or land the
  full form with M5.

## Risks / notes

- **Manual-override conflict — RECOMMENDATION, flag for the user to ratify.** If the author hand-moves or
  hand-scales a driven channel in the editor, the recommendation is **the constraint wins**: the
  `[ExecuteAlways]` component re-derives and reverts on the next frame, and because driven channels are
  suppressed from scene→code the manual edit never reaches source (mirrors RectTransform's driven
  props). To change size/placement the author edits the `Sizer`/`Snapper` intent or moves the surface;
  the **free** axes (e.g. a `down`-only Snapper's X/Z) remain hand-movable and sync normally. **OPEN:**
  confirm "constraint wins" over the alternative "a manual edit detaches the constraint".
- **`DrivenPropertyManager` for greying plain-Transform fields is internal.** RectTransform has a public
  tracker; plain scale/position driving would need reflection into an internal API (unverified across
  versions). Treated as a cosmetic enhancement, not load-bearing — the enforcement is the re-evaluation
  revert, which needs no such API. **OPEN / unverified.**
- **`[ExecuteAlways]` per-frame raycast cost.** A scene of many Snappers each casting every editor frame
  is wasteful; gate recompute on `transform.hasChanged` and a surface-dirty check. Editor-only, but real
  at scale — noted so the pipeline builds the gate in, not as a follow-up.
- **Build-strip correctness depends on baked values being current.** The `IProcessSceneWithReport`
  stripper forces one final `Evaluate()` before `DestroyImmediate` precisely so a stale editor frame
  cannot ship a wrong transform. If a Sizer/Snapper input (mesh/surface) is itself missing at build
  time, strip leaves the last good bake and logs — it never leaves a live constraint in the player.
- **Raycast needs colliders; the bounds fallback is approximate.** A collider gives an exact surface; the
  renderer/mesh-bounds fallback snaps to an axis-aligned box, so a concave or sloped surface rests by its
  AABB. Acceptable for the "on the floor / against the wall" intent this serves; precise-hull fitting is
  out of scope.
- **Multiple snapped objects do not de-collide** (out of scope) — two Snapper objects can overlap;
  each is correct against scene surfaces, not against each other.
- **Depth (world Z) is intentionally not a snap axis.** The horizontal/vertical model leaves Z for the
  author's `.Transform(pos:)`. **OPEN:** confirm no `forward/back` snap is wanted (a wall-mount case
  might want it; deferrable).
- **Component final names — OPEN.** Chosen `Sizer` and `Snapper` (verb-free nouns, symmetric with
  `.Transform`/`.RectTransform`, LLM-readable). Alternatives `SurfaceSnap`/`FitSize` were considered;
  ratify or rename before the pipeline runs.
- **Non-mesh objects** produce a located error, never a bounds-of-nothing guess — consistent with §7
  fail-loud.
