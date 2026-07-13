# M11 — Animation: common easing patterns (v0)

### Additions to the contract

M11 introduces a declarative animation-clip spec that Core lowers to keyframe data. All asset
references reuse §3 `AssetRef` verbatim; the generated clip is addressed like any other project asset.

```
AnimationClipSpec                               // authored, code-native; Core lowers to keyframes
  LogicalId         : string                    // stable anchor (§4), for round-trip
  Name              : string                    // clip name → asset file name
  Duration          : float                     // seconds
  Tracks            : AnimationTrack[]           // ordered

AnimationTrack
  PropertyPath      : string                    // serialized curve binding, e.g. "m_LocalPosition.y"
  TargetType        : TypeRef                    // §3 TypeRef of the animated component (e.g. Transform)
  From              : ValueNode                  // start value (Primitive/Vec*/Color), §3
  To                : ValueNode                  // end value, §3
  Easing            : EasingKind                 // preset from the fixed library below

EasingKind = one of                             // the fixed v0 library
  Linear
  InQuad   | OutQuad   | InOutQuad
  InCubic  | OutCubic  | InOutCubic
  InQuart  | OutQuart  | InOutQuart
  InSine   | OutSine   | InOutSine
  InExpo   | OutExpo   | InOutExpo
  InBack   | OutBack   | InOutBack
  InBounce | OutBounce | InOutBounce
  InElastic| OutElastic| InOutElastic

GeneratedClipRef                                 // the materialized asset, referenced by components
  Clip              : AssetRef                   // GUID:fileID of the generated .anim asset (§3 AssetRef)
  SourceSpecId      : string                     // LogicalId of the AnimationClipSpec that produced it
```

A component (`Animator` / `Animation`) references a generated clip through a §3 `AssetRef` inside its
`Fields` — the clip is a normal project asset once generated; only its *generation* is new here.

---

## Goal
Let an author declare a simple animation as a set of common easing curves over named properties for a
duration; Core lowers it to deterministic keyframes, the Editor writes it as an `AnimationClip` asset,
and a component references that asset via `AssetRef` (M4).

## In scope
- The `AnimationClipSpec` model (name, duration, `AnimationTrack[]`).
- A **fixed** easing library (the `EasingKind` values above) lowered to parametric `AnimationCurve`
  keyframe generation — deterministic given `(from, to, duration, easing)`.
- **Materialize** (code→asset): generate / update the `AnimationClip` asset from the spec; regenerate
  when the spec changes.
- Reference the generated clip from an `Animator` or `Animation` component via `AssetRef` (M4 path).
- **Reconcile** — LIMITED: detect that a generated clip was hand-edited away from any easing preset and
  surface a conflict/flag; do **not** reverse-engineer arbitrary curves back into an easing.
- Canonical, deterministic keyframe output (same spec → identical keyframes, byte-stable).

## Out of scope (all → `needs_research`)
- Multi-keyframe hand-authored curves (arbitrary keyframe sets, custom tangents beyond preset output).
- Animation **events** (`AnimationEvent`).
- `AnimatorController` state machines, transitions, parameters.
- Blend trees.
- Reverse-engineering arbitrary hand-edited curves back into an easing preset (only divergence
  *detection* is in scope, not inversion).

## Core deliverables

### Types added/changed (referencing §3)
- `AnimationClipSpec`, `AnimationTrack`, `EasingKind`, `GeneratedClipRef` (above).
- `AnimationTrack.TargetType` is a §3 `TypeRef`; `From` / `To` are §3 `ValueNode`s (`Primitive` for
  scalar tracks such as a float property, `Vec3`/`Color` for structured — lowered per-component-axis
  into one binding+curve each). The clip reference on the animator component is a §3 `AssetRef`.
- No change to `SceneModel`, `GameObjectNode`, `ComponentData`, `ValueNode`.

### Functions / behaviors (testable contracts)
1. **Easing → keyframes (deterministic).** For each `EasingKind`, given `(from, to, duration)`, Core
   emits a fixed, documented keyframe set. `Linear` = 2 keys with matching linear tangents. Polynomial
   / sine / expo easings = a fixed sampling (documented key count) with tangents computed from the
   easing derivative so playback matches the curve. `Back` / `Elastic` / `Bounce` emit the
   preset-specific key set (bounce = its segment breakpoints; elastic/back = overshoot samples). Output
   is byte-identical across runs.
2. **Endpoint exactness.** Every generated track starts exactly at `From` at t=0 and ends exactly at
   `To` at t=`Duration`, for all easings (overshooting easings still land on `To` at the end).
3. **Structured track expansion.** A `Vec3`/`Color` track lowers to one binding+curve per axis/channel
   (`.x`,`.y`,`.z` / `.r`,`.g`,`.b`,`.a`), each independently eased.
4. **`AnimationClipSpec` → clip data.** A spec lowers to a clip-data POCO (name, length, per-binding
   curves) that the Editor writes verbatim; binding = `(PropertyPath, TargetType)`.
5. **Spec change → regeneration.** Changing any of `Duration`, a track's `From`/`To`/`Easing`/
   `PropertyPath` changes the lowered keyframe set (and thus triggers asset regeneration); an unchanged
   spec lowers to identical data (no spurious regeneration).
6. **Clip reference wiring.** The generated clip is referenced from the `Animator`/`Animation`
   component as an `AssetRef`; materialize sets that reference (M4 `SetAssetRef` op).
7. **Hand-edit divergence → conflict (asymmetric reconcile).** Given clip data read back from a live
   clip, Core checks whether it matches the keyframes any `EasingKind` would generate for the track's
   endpoints/duration. If it matches a preset → no-op. If it does **not** match any preset → Core emits
   a **conflict/flag** (per §7) naming `clip > propertyPath`, stating the curve no longer corresponds to
   an easing preset. Core never attempts to infer an easing from arbitrary curves.

### Behavior note — asymmetry (stated clearly)
Materialize is authoritative code→asset: it generates and overwrites the clip from the spec. Reconcile
is **not** symmetric: it can only *classify* a live clip as "still a known preset" or "diverged," and on
divergence it raises a conflict. It does **not** patch the spec from an arbitrary hand-edited curve.

## Editor adapter deliverables
- **Write** an `AnimationClip` asset from lowered clip data: create/overwrite the `.anim`, set clip
  length, and bind each `(PropertyPath, TargetType)` to its `AnimationCurve` via
  `AnimationUtility.SetEditorCurve` (or `EditorCurveBinding` + curve). Save via `AssetDatabase`.
- **Read** a live clip's `EditorCurveBinding`s and curves back into clip-data POCOs for the divergence
  check (Core does the classification; the adapter only reads curves).
- **Resolve** the generated clip's path ↔ GUID (M4) so the component reference is a stable `AssetRef`.
- **Set** the clip reference on the `Animator`/`Animation` component via the Plan's `SetAssetRef` op.
- No easing math or classification in the adapter — Core owns keyframe generation and preset matching.

## Authoring API added
Fluent `Animate` verb on a GameObject handle:

```csharp
scene.Add("Door")
     .Animate("Open", 0.5f, a => a.Move(from:(0,0,0), to:(0,3,0), Ease.OutBounce));
```

- `Animate(name, duration, cfg)` lowers to an `AnimationClipSpec` (`Name`, `Duration`, `Tracks`).
- Track helpers: `a.Move(from, to, ease)` → a `Transform` `m_LocalPosition` `Vec3` track;
  `a.Scale(...)`, `a.Rotate(...)`, and a generic `a.Property(path, targetType, from, to, ease)` for
  arbitrary serialized properties.
- `Ease.*` maps 1:1 to `EasingKind` (`Ease.OutBounce` → `OutBounce`, etc.).

## IdentityMap / sidecar changes
- The generated clip is a project asset; its GUID is recorded in the sidecar `Assets[]` cache
  (`Guid, LastKnownPath, TypeHint="AnimationClip"`) so the `AssetRef` display path is re-derivable
  (§4), exactly like any M4 asset.
- Each `AnimationClipSpec` is anchored by its `LogicalId` in `Entries[]` (Kind = a spec anchor) so
  Reconcile can locate the authoring call to flag on divergence. `GeneratedClipRef.SourceSpecId` ties
  the asset back to the spec that produced it.
- The generated `.anim` lives at a deterministic path derived from the owning object + clip name (so
  regeneration overwrites the same asset rather than orphaning).

## Core test plan (RED behaviors)
1. Each `EasingKind` → its documented keyframe set for a fixed `(from,to,duration)`; snapshot-stable
   across runs. `Linear` = exactly 2 keys; `OutBounce`/`InElastic`/`OutBack` = their preset key counts.
2. Endpoint exactness: first key value == `From` at t=0, last key value == `To` at t=`Duration`, for
   every easing including overshooting ones.
3. `Vec3` track expands to three bindings (`.x/.y/.z`), each eased independently.
4. `AnimationClipSpec` → clip-data POCO: name, length, per-binding curves match expectation.
5. Spec change (duration / from / to / easing / path) → different keyframe data; identical spec →
   byte-identical data (no spurious regeneration).
6. Clip reference: materialize plan contains a `SetAssetRef` binding the generated clip onto the
   `Animator`/`Animation` component.
7. Divergence: clip data equal to a preset's output → classified as that preset (no conflict);
   clip data not matching any preset for the track endpoints/duration → located conflict emitted.
8. Reconcile never infers an easing: a hand-edited arbitrary curve produces a conflict, not a spec edit.

## Unity confirmation checklist
1. Author `scene.Add("Door").Animate("Open", 0.5f, a => a.Move((0,0,0),(0,3,0), Ease.OutBounce))` and
   Materialize. **Expected:** an `Open.anim` asset is generated, referenced by the Door's
   `Animator`/`Animation`, and plays a 0.5s bounce from y=0 to y=3.
2. Change the easing in code to `Ease.InOutCubic` and Materialize again. **Expected:** the same clip
   asset updates in place; motion is now an ease-in-out cubic over the same endpoints/duration.
3. Change `duration` to `1.0f` and Materialize. **Expected:** clip length becomes 1.0s; keyframe times
   rescale.
4. Open the generated clip in the Animation window and hand-edit a keyframe into a shape that is not any
   easing preset; run Reconcile. **Expected:** the tool flags the divergence (clip no longer matches an
   easing preset, naming clip + property) and does NOT silently rewrite the spec or the clip.
5. Undo the hand-edit back to the preset shape; run Reconcile. **Expected:** no conflict — clip
   classified as the known preset again.

## Dependencies
- **M4** — asset references (the generated clip is referenced via `AssetRef`; path↔GUID resolve/persist).
- **M3** — component fields / serialized property paths (track `PropertyPath`, animator component setup).
- **M1** — object creation / handles (the `Animate` verb hangs off a GameObject handle).

## Risks/notes
- Tangent computation for polynomial/sine/expo easings must match the easing's analytic derivative so
  the sampled curve reproduces the intended motion; the fixed key count per easing is documented and
  frozen for v0 determinism.
- `Bounce`/`Elastic`/`Back` are defined by their standard parametric forms; the exact segment
  breakpoints and overshoot constants are pinned in Core so keyframe output is reproducible.
- Divergence detection compares live curves against regenerated preset curves within a tolerance; the
  tolerance is pinned so classification is deterministic and does not flake.
- Everything under Out of scope is a `needs_research` stub (foundation §10 "advanced animation content
  authoring"), not a hidden partial feature — the tool must refuse, located, rather than half-generate.
