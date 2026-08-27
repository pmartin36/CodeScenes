# Spec 53: the spatial solvers size and place instanced prefabs (whole-hierarchy bounds)

The whole point of the spatial solvers is "drop an asset in, size and rest it by relationship, never
hand-measure" (`codescenes-authoring` skill, "Place and size by relationship"). That workflow BREAKS for
the most common real-world asset: a downloaded prefab instanced with `scene.Instance(...)`. Observed live
in the house-demo one-shot (SceneBuilderOneShot, session `e0e6d478`): the author instanced a sofa,
chained `.FitSize(width: 2f).AlignTo(floor, y: AxisAlign.AbutMax)`, and got a clean console, ZERO solver
components in the scene, and a sofa left at native scale. It fell back to a hand-written `PropMeasure.cs`
that walks the prefab's children, encapsulates their bounds, and bakes scale + floor-rest into explicit
`Transform()` calls — re-implementing, by hand, exactly what the solver should do. That workaround is the
anti-pattern the solvers exist to eliminate.

Three stacked defects, in load-bearing order. No single fix delivers the user-visible outcome alone.

## The measured defects

1. **The solvers read bounds from a Renderer/MeshFilter on their OWN GameObject only — never the
   children.** An imported prefab keeps its mesh in child nodes, so the solver finds nothing on the
   instance root and does nothing.
   - `FitSize.Evaluate` calls `GetComponent<MeshFilter>()` on itself (`com.codescenes/Runtime/FitSize.cs:80`);
     if absent it logs "has no MeshFilter/mesh to size" and returns (`:81-89`); sizes from
     `mf.sharedMesh.bounds` (`:91`) — one mesh.
   - `AlignTo.Evaluate` calls `GetComponent<Renderer>()` on itself (`com.codescenes/Runtime/AlignTo.cs:123`);
     if absent logs "has no Renderer/mesh bounds" and returns (`:124-132`); the align TARGET uses
     `target.GetComponent<Renderer>()` (`:195`) and returns silently if the target root has none (`:196`)
     — so a nested-mesh instance also fails as an align target.
   - `Between.Evaluate` calls `GetComponent<Renderer>()` on self and both anchors
     (`com.codescenes/Runtime/Between.cs:110-112`); returns if any is missing (`:114-122`).
   - `ProjectedExtent` operates on a single `r.localBounds` / a passed `localBounds`
     (`com.codescenes/Runtime/ProjectedExtent.cs:14-33`). No `GetComponentsInChildren` and no child-bounds
     encapsulation exists anywhere in `com.codescenes/Runtime/` (grep-confirmed). THIS is the load-bearing
     blocker: even a correctly-attached solver is inert on a nested-mesh instance.

2. **The solver verbs do not exist on `InstanceHandle`, so the fluent form is silently discarded.**
   `FitSize`/`AlignTo`/`Between` are declared only on `NodeHandle` (`com.codescenes/Runtime/NodeHandle.cs:52,66,82`);
   `InstanceHandle`/`SceneObjectHandle` have none. CodeScenes builds from PARSED source text, not a
   semantic compile, so the type error never surfaces. The parser routes the chained verb into
   `ApplyFitSize`/`ApplyAlignTo`/`ApplyBetween`, which add to `node.Components`
   (`SceneBuilder.Core/Parsing/BuilderParser.Spatial.cs:97,223,384`); but `BuildInstanceNode` hard-codes
   `Components = System.Array.Empty<ComponentData>()` (`SceneBuilder.Core/Parsing/BuilderParser.Instance.cs:235`),
   so `node.Components` is never read for an instance and the solver is dropped. The recognizer ACCEPTS
   the verb with no receiver-kind guard (`SceneBuilder.Grammar/FlatShapeRecognizer.cs:221-229` via
   `ApplyChainedCalls`), so no SB1002 is raised — the console stays clean. Note: the spec-51
   `.Component<FitSize>()` path is NOT dropped (it lowers to `AddedComponents`, carried by
   `BuildInstanceNode:240`), but it attaches to the root and is inert per defect 1.

3. **A discarded builder call is silent, not located.** The combination of "recognizer accepts" +
   "parser stores into a collection `BuildInstanceNode` discards" means a solver verb on an instance
   vanishes with a clean console — a prop left unsized and no signal why. That is the "opt-in safety
   regresses silently" failure the operating contract warns against.

## The fix

Owner of the load-bearing fix: the shared bounds kernel `ProjectedExtent`
(`com.codescenes/Runtime/ProjectedExtent.cs`), which all three solvers already route through, so every
solver inherits the fix by default — no per-solver opt-in.

1. **Whole-hierarchy bounds (the unblock).** The solvers resolve their extent from ALL renderers/mesh
   filters in the object's hierarchy (`GetComponentsInChildren`), combining each child's mesh bounds
   expressed in the SOLVER OBJECT's local space (accounting for each child's local transform relative to
   the root), not a naive world-space AABB (so a rotated root is not inflated — `ProjectedExtent` already
   holds the rotation/scale-aware form). A single-mesh object (one renderer on the root, no children)
   yields exactly today's bounds, so the change is backward-compatible; there is no fallback branch,
   aggregation IS the rule. Owner: a new combined-bounds primitive on `ProjectedExtent`, consumed by
   `FitSize.Evaluate`, `AlignTo.Evaluate` (self AND the align target), and `Between.Evaluate` (self AND
   both anchors). The "has no renderer/mesh" located error remains only when the hierarchy has NO
   renderer at all.

2. **Solver verbs on `InstanceHandle`, routed to `AddedComponents`.** Add `FitSize`/`AlignTo`/`Between`
   to `InstanceHandle` (and `InstanceHandle<TRef>`), returning `InstanceHandle`, mirroring how spec 51
   put `Component<T>` there. The parser lowers a solver verb on an instance receiver into
   `node.AddedComponents` (carried by `BuildInstanceNode:240`), NOT `node.Components` (discarded at
   `:235`), through the same per-verb lowering the node path uses. The recognizer must accept the same
   shape (its `Scope` already tracks instance-ness for the spec-51 instance verbs), so
   `RecognizerAgreementTests`/`RecognizerCompletenessTests` stay green.

3. **No silent discard (invariant + guard).** `node.Components` being non-empty for an instance node is
   unreachable by construction once fix 2 routes solver verbs to `AddedComponents`; make that an enforced
   invariant — `BuildInstanceNode` must not silently drop a non-empty `node.Components`, and any builder
   call on an instance receiver that is not routed to a real destination is a LOCATED error
   (SB1002-shaped, naming the call), never accepted-then-discarded. The check that fails on bypass: a
   test that a solver verb on an instance produces an actual `AddedComponents` entry (fix 2) AND a test
   that any unrouted call on an instance is a located error rather than a clean-console no-op.

## Accept when

Proven by Core parse/recognize + bounds tests AND an EditMode gate test in
`unity-gate/Assets/GateTests/` against a REAL nested-mesh prefab instance in a live editor (the boundary
the headless suite is blind to):

- **The workflow works, live.** A prefab whose mesh lives in CHILD nodes, instanced with
  `scene.Instance(...)` and given `.FitSize(width: 2f)` + `.AlignTo(floor, y: AxisAlign.AbutMax)`, builds
  so the instance's WHOLE-hierarchy world width is ~2 m and its combined-bounds bottom rests on the floor
  top — asserted from the live combined `Renderer.bounds` of the instance, not a mock. This is the exact
  case `PropMeasure.cs` worked around; with the fix, no hand-measuring is needed.
- **Whole-hierarchy bounds on a plain node too.** A `NodeHandle` with a multi-renderer child hierarchy
  sizes/rests by the combined bounds, and a single-mesh node is byte-identical to today's behavior (a
  regression guard: no change to the single-renderer result).
- **Rotation is not inflated.** A rotated nested-mesh object sizes to its true oriented extent, not a
  world-AABB (the local-space combination), proven against a yaw-rotated instance.
- **Instance solver verbs parse and are not dropped.**
  `scene.Instance("x.glb").FitSize(width: 2f).AlignTo(t, y: AxisAlign.AbutMax)` and the statement form
  over a captured instance both parse, recognize with no SB1002, and produce real `AddedComponents`
  entries; parser and recognizer agree (agreement tests green).
- **No silent discard.** A builder call on an instance receiver that maps to no destination is a located
  error naming the call, not a clean-console no-op; a permanent guard asserts an instance node never
  drops a non-empty `node.Components`.
- **Round-trips.** A code-authored instanced prop with solvers builds, and a scene edit syncs back the
  `.FitSize`/`.AlignTo` calls on the instance that re-sync byte-stable (PatchEdits == 0).

## Out of scope

- **Authoring materials.** The props render correctly only because they are instanced WHOLE (embedded
  materials preserved); a plain node carrying just a mesh sub-asset has no material and renders magenta.
  That is why instancing is the right primitive for imported props, and why the solvers must work on
  instances — but authoring `.mat` assets or material assignment is a separate boundary, not this spec.
- **The `NodeHandle` solver API shape** is unchanged; this spec adds the instance surface and changes the
  bounds source, nothing else.
- **No new solver behavior** beyond whole-hierarchy bounds — no auto-orient, no per-child sizing.
