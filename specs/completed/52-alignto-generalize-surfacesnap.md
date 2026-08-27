# Spec 52: AlignTo — generalize and rename SurfaceSnap to per-axis extent alignment

`SurfaceSnap` only ever expresses OUTSIDE-face contact: each of its options snaps my face to the
target's OPPOSING face (`mine + theirs = 1`). That covers "rest on the floor," but not aligning my
edge to the target's SAME edge (tops flush, bottoms flush) or centering, which is what abutting
modular terrain needs (place the next green so its top continues from the previous green's top). This
spec generalizes the component to a named per-axis alignment with an optional world-unit offset,
evaluated in a reference FRAME (default: the target's local space, so a rotated/sloped target is
handled correctly rather than snapping to a world-axis-aligned box), and renames the component to
`AlignTo`. The product is pre-launch (no external users, no back-compat alias needed), so this is a
clean rename plus a model change.

Owner-file inventory and current-symbol citations are grounded in the SurfaceSnap surface as it stands
on `main`.

## The model

Per world/frame axis X/Y/Z, an author picks an alignment `Mode` and an optional `offset`:

| Mode | my feature -> target feature | replaces (SurfaceSnap) |
|---|---|---|
| `None` | free (axis left to the transform) | the `*.None` default |
| `AbutMin` | my MAX face -> target MIN face (I sit outside, on the target's min side) | Up / Right / Forward |
| `AbutMax` | my MIN face -> target MAX face (I sit outside, on the target's max side) | Down / Left / Back |
| `AlignMin` | my MIN face -> target MIN face (near faces flush) | (new) |
| `AlignMax` | my MAX face -> target MAX face (far faces flush) | (new) |
| `AlignCenter` | my CENTER -> target CENTER | (new) |

- `None` is enum index 0 and is the inert default: an unpinned axis prunes to nothing emitted, exactly
  as `Vertical/Horizontal/Depth.None` does today (the default-omission invariant the read side relies
  on — `SurfaceSnap.cs` "None must be index 0" and `SpatialComponents.cs:208`).
- **Offset**: an optional world-unit translation applied ALONG that axis AFTER the alignment. Sign is
  the frame axis's POSITIVE direction (uniform: `+` is toward axis-positive for every mode; there is no
  per-mode sign flip). Authored fluently as `AlignMax.Offset(0.2f)`; absence means offset 0.
- **Frame** (the axes and space the alignment and offset are computed in):
  - Default (no frame arg, target present): the TARGET's local space. So `AlignTo(green, y: AlignMax)`
    aligns along the green's own up, correct for a sloped/rotated target.
  - `frame:` override: a supplied `SceneObjectHandle` whose transform's local axes are used instead of
    the target's (for the case where the target ROOT is not oriented like the surface — point `frame:`
    at the child/transform that is).
  - `space: World`: world axes (the axis-aligned, rotation-agnostic case). This is today's behavior.
  - No-target (raycast/scan) mode has no target frame, so it evaluates in WORLD axes (or `frame:`/`space`
    if supplied) and supports ONLY `AbutMin`/`AbutMax` (contact with the found surface); `AlignMin/Max/
    Center` require a target's extent and yield a located error when authored without a target.

This differs from `Between`'s WORLD default deliberately: `Between` has two anchors and no single target
frame, whereas `AlignTo` always has a target to borrow a frame from, and "relative to what you latch
onto" is the intuitive default.

### Builder API (compiles for IntelliSense; parsed, not executed)

```csharp
// com.codescenes/Runtime/NodeHandle.cs — facade, body is `=> this;`
public NodeHandle AlignTo(
    SceneObjectHandle target,
    AxisAlign x = default,
    AxisAlign y = default,
    AxisAlign z = default,
    SceneObjectHandle frame = null,
    AlignSpace space = AlignSpace.TargetLocal) => this;
```

```csharp
// com.codescenes/Runtime — a UnityEngine-free authoring value type (partial-class pattern like
// Between.Axis, so NodeHandle's signature and the parser can reference it without Unity).
public readonly struct AxisAlign
{
    public static readonly AxisAlign None = default;      // index-0 inert default
    public static readonly AxisAlign AbutMin;
    public static readonly AxisAlign AbutMax;
    public static readonly AxisAlign AlignMin;
    public static readonly AxisAlign AlignMax;
    public static readonly AxisAlign AlignCenter;
    public AxisAlign Offset(float worldUnits);           // returns a copy carrying the offset
}
public enum AlignSpace { TargetLocal, World }
```

Everyday call, unchanged in spirit from the design discussion:
```csharp
node.AlignTo(green, x: AxisAlign.AlignCenter, y: AxisAlign.AlignMax, z: AxisAlign.AbutMax);
node.AlignTo(green, y: AxisAlign.AlignMax, z: AxisAlign.AbutMax.Offset(0.5f));  // 0.5 gap on Z
```
(`using static SceneBuilder.Authoring.AxisAlign;` lets the presets read bare — `AlignMax` — in docs/examples.)

## Owner

- **Runtime component** `com.codescenes/Runtime/SurfaceSnap.cs` -> `AlignTo.cs`: the MonoBehaviour, its
  per-axis serialized fields (replace the three direction enums with per-axis `Mode` + `offset`), the
  frame resolution, `IPositionDriver`, `[ExecuteAlways]`, `[DefaultExecutionOrder(-90)]`,
  `captureThreshold`, `ResetBaseline`, and its published XML doc comments.
- **Authoring value types** `com.codescenes/Runtime/`: new `AxisAlign` (UnityEngine-free partial, like
  `Between.Axis`) and `AlignSpace`.
- **Builder facade** `com.codescenes/Runtime/NodeHandle.cs`: replace `SurfaceSnap(...)` with `AlignTo(...)`.
- **Core by-name contract** `SceneBuilder.Core/Model/SpatialComponents.cs`: `SurfaceSnapTypeName` ->
  `AlignToTypeName` (`SceneBuilder.Authoring.AlignTo`), the field-name constants and enum-type/member
  constants, the axis mask, and the keyword mapping table (see Mechanism).
- **Parser** `SceneBuilder.Core/Parsing/BuilderParser.Spatial.cs` + dispatch in `BuilderParser.cs`
  (:346, :441-442).
- **Recognizer** `SceneBuilder.Grammar/FlatShapeRecognizer.Spatial.cs` + dispatch in
  `FlatShapeRecognizer.cs` (:224-225). Parser and recognizer MUST move together (pinned by
  `RecognizerAgreementTests`/`RecognizerCompletenessTests`).
- **Emitter** `SceneBuilder.Core/Reconcile/SpatialComponentSource.cs`: `IsSpatial`, `MethodName`,
  `RankFor`, and a DEDICATED argument-render arm for `AlignTo` (modeled on `RenderBetweenArguments`,
  not the generic per-field arm), grouping each axis's `(mode, offset)` into one `x:`/`y:`/`z:` keyword.
- **Editor adapter references**: `SurfaceSnapEditor.cs`, `LiveTransformRead.cs`, `SpatialBuildStripper.cs`,
  `PlanExecutor.cs` (`ResetBaseline` call), `SerializedFieldExclusions.cs`, `ComponentDefaultTemplate.cs`.
- **Analyzer** `CodeScenes.Analyzers/SpatialAuthorityAnalysis.cs`: the SurfaceSnap keyword-axis table and
  `"SurfaceSnap"` method dispatch.
- **Kernel** `com.codescenes/Runtime/ProjectedExtent.cs`: reused as-is (its rotation/scale overload is
  the frame-aware projection); no rename (internal, behavior-neutral).

## Mechanism

**Runtime evaluation.** For each pinned axis, resolve the frame axis direction (target-local by default,
`frame:` transform, or world per `space`). Using `ProjectedExtent.HalfExtentAlong` compute MY extent-point
for the mode's my-feature (min = `center - half`, center, max = `center + half`) and the TARGET's
extent-point for the mode's target-feature along that same frame axis; translate so they coincide, then
add `offset * frameAxisUnit`. No-target modes raycast/scan along the axis to a surface and rest my face on
it (today's `RaycastSurface`/`FallbackScanSurface`, unchanged). Preserve: live re-snap when the target/
frame transform moves (the `_needsSnap || transform.hasChanged || surface.hasChanged` gate), the
`captureThreshold` sticky-detach, `PositionAuthority.ResolveOwned` per-axis arbitration and its single
warning, `PriorityOrder`/execution order `-90`, and `ResetBaseline` for the raw-write baseline.

**Core by-name contract.** `AlignTo` is written by the generic by-name Materialize path (no per-component
materializer arm). Per axis, two serialized fields carry the pin: a `Mode` enum field and a `float` offset
field (e.g. `xMode`/`xOffset`, `yMode`/`yOffset`, `zMode`/`zOffset`), plus `target`, `frame`, `space`,
`captureThreshold`. `SpatialComponents` declares: `AlignToTypeName = "SceneBuilder.Authoring.AlignTo"`;
the field-name constants for the six axis fields + target/frame/space; the `Mode` enum type FullName
(`SceneBuilder.Authoring.AlignTo+Mode`) and its member names (`None/AbutMin/AbutMax/AlignMin/AlignMax/
AlignCenter`); the `AlignSpace` type FullName + members. A pinned-axis predicate (`Mode != None`) replaces
`SurfaceSnapMask`'s `(verticalSet, horizontalSet, depthSet)`, driving `node.DrivenChannels` (X->PositionX,
Y->PositionY, Z->PositionZ). The gate reflection test (below) asserts these Core constants equal the
runtime enum FullNames/members.

**Parse + recognize (move together).** An axis keyword `x:`/`y:`/`z:` takes an `AxisAlign` value: a
preset member access (`AxisAlign.AlignMax` or a bare `AlignMax`) optionally followed by `.Offset(<float
literal>)`. The parser lowers it to the axis's `(Mode member, offset literal)`; a non-literal offset or an
unknown member is a located `Unsupported`. `target:` and `frame:` take object refs; `space:` takes an
`AlignSpace` member. `AlignMin/AlignMax/AlignCenter` authored with no `target:` is a located error. The
recognizer re-declares the same keyword/preset literals (Grammar cannot reference Core) and accepts the
same shapes; `RecognizerAgreementTests` prove they agree on every accept/reject.

**Emit (byte-stable).** The dedicated `AlignTo` arm renders only pinned axes, each as `x: AxisAlign.<Mode>`
or `x: AxisAlign.<Mode>.Offset(<f>f)` when offset != 0, plus `target:`, and `frame:`/`space:` only when
non-default. Order is deterministic (`RankFor`: FitSize < AlignTo < Between preserved). A converged builder
re-syncs to ZERO edits (the reverse map is total over the mode/offset pairs, mirroring the current
enum-field reverse map).

## Invariant and check

- **Rename is total.** No `SurfaceSnap` identifier (type, method, field constant, enum, keyword,
  filename) remains anywhere in the repo after this ships. Enforced by a guard test (a Roslyn/text scan,
  sibling to the existing spatial guards) that FAILS if `SurfaceSnap` appears in tracked source, so a
  missed call site cannot slip through.
- **Enum contract agreement.** An EditMode reflection test asserts `typeof(AlignTo.Mode).FullName` and
  each member equal the `SpatialComponents` Core string constants (as the current
  `RoundTripSpatialTests` does for the SurfaceSnap enums), so the by-name write contract cannot silently
  drift.
- **Round-trip fixed point.** A created node carrying an `AlignTo` emits an `AlignTo(...)` call whose
  second sync produces zero edits (byte-stable), asserted in both Core and EditMode.

## Accept when

A Core parse/recognize/emit suite and an EditMode gate suite (against a live editor scene) prove, with
the offline gate green:

- **Existing behavior preserved (regression):** `AbutMax` on a floor rests my bottom face on the floor top
  (the migrated `SurfaceSnap_DownOnFloor...` case); `AbutMin` onto a ceiling; a two-axis `AbutMax` corner;
  no-target raycast and no-collider fallback-scan still land; `FitSize`-before-`AlignTo` order; live
  re-snap when the floor moves; within-threshold re-snap and beyond-threshold sticky detach;
  `PositionAuthority` arbitration composes/yields exactly as the current `SpatialAuthorityRuntimeTests`.
- **New capability (the minigolf case), live:** a node `AlignTo(green, x: AlignCenter, y: AlignMax,
  z: AbutMax)` places with its top face co-planar with the green's top, centered across the green on X,
  and abutting just past the green's max-Z end, observed on real `transform.position`/bounds. `AlignMin`
  puts bottom faces flush. `AbutMax.Offset(0.5f)` opens a 0.5-unit gap along the frame axis (positive =
  frame-axis positive).
- **Frame:** with a rotated target, the default target-local frame aligns along the target's own axes
  (not world), proven against a yaw/tilt-rotated target; `space: World` reproduces world-axis behavior; a
  `frame:` transform override aligns along that transform's axes.
- **Located failures:** `AlignMax` with no `target:` yields a located error naming the axis; a non-literal
  `.Offset(...)` argument is refused located; parser and recognizer agree on both (agreement tests green).
- **Round-trip both directions:** a code-authored `AlignTo` builds; a scene edit syncs back an
  `AlignTo(...)` call that re-syncs byte-stable (PatchEdits == 0).
- **Rename guard green:** no `SurfaceSnap` symbol remains; the enum-contract reflection test passes.

## Migration (part of this spec)

- Rename every reference in the Owner inventory and the tests/fixtures that exercise SurfaceSnap
  (`RoundTripSpatialTests`, `RoundTripSpatialSyncTests`, `RoundTripSpatialThresholdTests`,
  `SpatialAuthorityRuntimeTests`, `SpatialComponentTests` + `.RoundTrip`, `DifferTests`,
  `RecognizerAgreementTests`, `SpatialAuthorityAnalysisTests`, and the `SpatialAuthoringExamplesFixture`
  call-sites) to `AlignTo`, converting each old direction to its `AbutMin`/`AbutMax` equivalent and adding
  coverage for the new `Align*`/offset/frame cases.
- The manual project `../Unity/SceneBuilderTest` `DemoScene.cs` `.SurfaceSnap(down: true)` -> `AlignTo`;
  its `DemoScene.sbmap.json` logicalIds embed the type FullName and REGENERATE on the next build (note so
  a stale sbmap is not mistaken for a defect). Update `TEST_PROCEDURE.md`.
- **Docs (do after gate + live-verify pass):** the `AlignTo` XML doc comments (published API copy), the
  `NodeHandle.AlignTo` doc + cross-ref, the skill `com.codescenes/Documentation~/codescenes-authoring/SKILL.md`
  and `authoring-api.md`, regenerate `SceneBuilder.DocGen` -> the site `../CodeScenesSite/src/content/api.json`
  via `npm run api:sync`.

## Out of scope

- **Conforming to a non-planar/sloped mesh surface + orient-to-normal** (resting ON a tilted green with
  rotation matched to the surface). That is the reserved `OrientToSurface` direction (spec 45's
  `RotationX/Y/Z` channels), a raycast/normal capability distinct from extent alignment.
- **Fraction back-solve on drag** (updating the pin from a manual move). `AlignTo` keeps SurfaceSnap's
  within-threshold re-snap / beyond-threshold detach; it does not back-solve a mode/offset from a drag.
- **`OurLocal` frame** (aligning in the placed object's own frame): circular, omitted.
- **Arbitrary two-sided fractions** (`my 0.37 to their 0.62`): the named `Mode` set plus `offset` is the
  surface; a raw fraction API is not added.
