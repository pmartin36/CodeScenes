# Spec 35 — Reference writes, sorting-layer convergence, and cache invalidation

Four defects found while building spec 33, each in a different subsystem and none depending on
another. They are filed here rather than absorbed into 33.

Each was raised as a claim and then adversarially audited against the tree. Where the audit refuted
the original claim and found the real defect underneath, this spec states the audited version. Two
were confirmed BY CONSTRUCTION — the code cannot do anything else — and that reasoning is given so a
future reader can check it without an editor.

## Build order

D1, D3 and D4 are SHIPPED (see each section). **D2 is the only item left to build.**

## D1 — a populated list of scene references writes null slots

**SHIPPED at `7640919 (plus 2c3dc36, the mixed-handle array-type fix its own emission exposed)`.** Do not re-plan D1; it is on `main` and gate-verified.

**Data loss on the happy path.** Authoring a `List<GameObject>` (waypoints, spawn points, targets)
with in-scene targets resizes the live array to N and leaves every element null. Silent: no
exception, no warning, no conflict.

Confirmed by construction, both halves:
- `SceneBuilder.Core/Materialize/Materializer.cs` (`EmitFieldOp`) has arms for a bare `ObjectRef`
  (→ `SetReference`), a bare `AssetRef` (→ `SetAssetRef`), and a `List` whose items are ALL
  `AssetRef` (→ per-index `SetAssetRef`). A `List` of `ObjectRef` matches none of them and falls to
  the final `else`, emitting a plain `SetField`.
- `com.codescenes/Editor/SerializedFieldBridge.cs` (`WriteProperty`) switches on Primitive, Enum,
  Vec2/3/4, Quat, Color, List, Nested and Unsupported. Its `List` arm sets `arraySize` then recurses
  per element. There is **no `ObjectRef` case and no `default:` arm**, so each element falls through
  the switch and nothing is written.

This is C9's shape reversed: not "my clear was ignored" but "my references vanished". C9 was rated
data loss on the happy path and took priority over its whole spec.

Uncovered: the only list-of-`ObjectRef` test in the repo covers lowering and never reaches diff,
materialize or write.

**Build:** a list of scene references round-trips by value, both directions, each element resolving
to its target.

**Accept when:** a two-element scene-reference list authored in code produces two correctly assigned
live elements; a second sync produces zero edits; clearing one element to `NodeHandle.None` clears
exactly that element and leaves the other assigned.

## D2 — an authored sorting layer never converges

**DECIDED (repo owner, approach (a)): excluded fields are ONE-WAY, made loud.** Keep the exclusion
and report a source-authored excluded field through spec 33's located report channel, so the author
is told the line does nothing. Approach (b) — pruning the line from their source — was rejected:
silently deleting code someone typed is its own surprise, and it is the more invasive change.

This item is the only one left to build in this spec.

**A regression introduced by spec 33's C6.** `SerializedFieldExclusions` excludes `m_SortingLayer`
on `UnityEngine.Renderer` and `SortingGroup`. Correct for its own purpose — one Inspector control
must not yield two authored fields — but it made the field unauthorable in a way that does not
fail loudly.

Confirmed by construction:
- The exclusion feeds BOTH the live read and the type template:
  `com.codescenes/Editor/ComponentDefaultTemplate.cs` builds the template through the SAME
  `SerializedFieldBridge.CollectFields` walk ("so template and creation cannot diverge").
- `SceneBuilder.Core/Diff/Differ.cs` computes `known` from the snapshot OR the type template. A
  source-authored `m_SortingLayer` is in neither, so `known` is false and a `SetField` is emitted —
  on every build, forever.
- `SceneBuilder.Core/Reconcile/ComponentReconciler.cs` iterates SNAPSHOT fields, so a source-only
  field is never visited and the line can never be pruned by a sync.
- The write cannot take effect anyway: the green
  `InspectorVisibilityRuleTests.ReadComponent_SortingLayerIndexAloneIsDiscardedByUnity` proves Unity
  discards a sorting-layer index alone.

Measured live during spec 33 (Unity 6000.5.3f1) for a body authoring `m_SortingLayer`:
`build1 ops=6, build2 ops=1, build3 ops=1, build4 ops=1`, with `sync1/sync2 patchEdits=0`.

This contradicts the product's core constraint: sync fires on every change and must reach a fixed
point. Here every debounced keystroke costs a plan op and an unconditional scene re-save.

No builder authors the field today, in this repo or the live test project, so nothing is broken
right now. The defect is that hand-authoring it becomes a silent, permanent non-convergence trap.

**Build (approach (a)).** A source-authored field that the adapter excludes is detected and
reported through spec 33's located report channel — the same channel C5 and C7 use, not a second
one — naming the object, the component type and the field, and saying the line has no effect. The
build must also stop emitting a plan op for it, or the console goes quiet while the scene keeps
re-saving on every keystroke, which is the actual defect.

The set of excluded fields lives adapter-side (`SerializedFieldExclusions`) and the differ that
emits the op lives in Core, so the two halves have to meet somewhere. That crossing is this item's
one design decision; it does NOT mean adopting approach (b), which additionally deletes the
author's line. Nothing here removes anything from the author's source.

**Accept when:** a builder authoring `m_SortingLayer` reaches a fixed point — either the line is
removed (b), or it survives with a located warning and the build stops emitting a plan op for it (a).
A gate test authors the field in builder source; today none does, which is why 580 tests stayed green
over it.

## D3 — the incremental snapshot cache is never invalidated when the IdentityMap changes

**SHIPPED at `2a634a8`.** Do not re-plan D3; it is on `main` and gate-verified.

`com.codescenes/Editor/ChangeScopedSnapshot.cs` declares that an incremental assemble must be
byte-equivalent to a cold read of the same scene state. Its node cache is only ever replaced
wholesale by an assemble; nothing keys it to an IdentityMap generation and there is no invalidation
site. A cached node is reused without re-reading any field, including an `ObjectRef` whose value is
entirely a function of the resolver.

Auto-sync hides this by cold-reading at every cycle tail. The manual **Sync Active Scene** and
**Build All** menu commands rewrite the IdentityMap and never touch the assembler cache, and neither
triggers an asset import or domain reload that would clear it.

Reachable sequence, every step an ordinary UI action with auto-sync armed:
1. An auto-sync cycle caches a node whose reference field targets an object not yet in the map, so it
   holds a raw `GlobalObjectId`.
2. The user invokes Sync Active Scene or Build All. The map gains that target's entry.
3. The user nudges an unrelated object. The next incremental assemble reuses the cached node.
4. A cold read at that instant would yield the target's LogicalId. The byte-equality contract is
   violated.
5. `ComponentReconciler.ClassifySnapshotRef` finds the raw id in neither the resolvable nor the
   pending set and returns `Dangling`: a console warning naming a raw GlobalObjectId, and the field
   patch is SUPPRESSED for that cycle. It self-heals on the following cycle.

The related fix in `fb6d1da` made the resolver a required per-assemble parameter, guarded by a
reflection test. That closed the omission hazard and did not touch cache lifetime.

**Build:** an incremental assemble cannot serve a node computed under a stale IdentityMap. The cache
is invalidated, or keyed, on the generation it was built against, at ONE site every assemble path
inherits — not by adding a call to each menu command.

**Accept when:** assemble cold, change the map, assemble incrementally, and the result is
byte-identical to a cold read. No spurious `DanglingReference`, no suppressed field patch. No test
varies the resolver between two assembles today, and the byte-equality test uses a scene with no
reference fields at all.

## D4 — value-patching preserves a typed selector that cannot take an instance handle

**SHIPPED at `225400d (plus 6018199, widening the authoring-surface scan)`.** Do not re-plan D4; it is on `main` and gate-verified.

`com.codescenes/Runtime/NodeHandle.cs` declares `public sealed class NodeHandle`;
`InstanceHandle` has no base, no interface and no conversion operator, and there are no implicit
operators or extension methods anywhere in the package. So `c.Set(x => x.target, tankInstance)` does
not compile while `c.Set("target", tankInstance)` does.

On its own that is an API asymmetry the author sees immediately. The defect is that SYNC CAN WRITE IT:
sync patches only the value expression inside a statement the author already wrote, preserving their
typed selector verbatim. Author writes `c.Set(x => x.target, door)`; the user rewires that field onto
a prefab-instance ROOT; sync rewrites only the argument; the file now reads
`c.Set(x => x.target, tank)` with an `InstanceHandle`, and does not compile.

C10's shape — already on disk, no self-heal, broken on every later sync — with conjunctive
preconditions rather than universal ones. A reference to an object INSIDE an instance's hierarchy has
no IdentityMap node entry and is omitted instead, so it does not reproduce.

The component APPEND path is NOT affected and is not the defect: every sync-emitted component `Set`
is hardcoded to the string spelling. Do not change it.

**Build:** an instance-target reference emitted into a typed selector produces source that compiles.

**Chosen: (a), one mechanism.** `SceneObjectHandle` is the base of both `NodeHandle` and
`InstanceHandle`, and every authoring parameter that accepts a scene-object reference is typed
`SceneObjectHandle`: `ComponentHandle<T>.Set(selector, …)`, `OverrideHandle.Set(selector, …)`
and `NodeHandle.SurfaceSnap(target:)`. The rule lives in the authoring type for every SCALAR
spelling, so no emit site decides it and none can bypass it. `SurfaceSnap(target:)` is a third
site of the same defect, found by that enumeration: it renders a pre-rendered handle the same
way, and an instance target there produced `CS1503`. An `InstanceHandle` overload was not
usable — `target` is one optional parameter among seven, so an overload differing only in its
type is unresolvable whenever the parameter is omitted.

A list of references is the one shape the authoring type cannot cover: C# infers an
implicitly-typed array's element type from its elements, never their common base, so `new[] {
door, tank }` (a plain node beside a prefab-instance root) does not compile even though both
are `SceneObjectHandle`. The same failure reaches an ordinary Inspector state with no second
kind involved at all — one instance-root target beside a cleared slot, `new[] { tank,
NodeHandle.None }` — because the cleared slot renders as a `NodeHandle` beside an
`InstanceHandle`. A list of scene-object-handle expressions is therefore an emit-site decision,
made at the single site `ListValueEmission.ArrayPrefix`: it renders an explicit `new
SceneBuilder.Authoring.SceneObjectHandle[] { … }` whenever every item is a handle expression,
whatever concrete kinds its elements are.

**Accept when:** a field authored in the typed spelling and then rewired onto a prefab-instance root
round-trips to source that compiles, and a second sync is a fixed point. `BuilderCompileCheck` stays
green across that sync.

**Determined: not reachable at the renderer.** `RenderOverrideSetCall` reads
`OverrideSetSpec.PropertyPath`, and the only site that builds an `OverrideSetSpec` is
`ReconcilerInstances.BuildOverrideSetSpec`, which copies the path verbatim from a SNAPSHOT
override. A snapshot override's path comes from `PrefabInstanceProbe` through
`SceneSnapshotReader`, and is Unity's `PropertyModification.propertyPath`, always a serialized
path. The `member:` sigil is a parse-side artifact of the MODEL, and a model override never
becomes an `OverrideSetSpec`; it emits a `DropInstanceCall`/`DropScopedOnCall` instead. The
sigil therefore does reach `SourcePatchApplier.Instances.cs`, as a drop call-match key in
`OverrideSetTargetsPath`, and never reaches the renderer. `AuthoredPathResolver` normalizes the
model-side sigil before any diff, a second and independent barrier on a path that does not reach
the renderer anyway. The site stays out of scope.

The chosen D4 mechanism covers it regardless of reachability: rendered through the sigil branch,
an instance-handle value emits `e.Set((Game.DoorOpener x) => x.target, tank)`, which binds
against the widened `OverrideHandle.Set(selector, SceneObjectHandle)`. Both spellings and both
renderer entry points (the root `.Override(...)` and the nested `.On(...)` twin) are pinned by
`SceneBuilder.Core.Tests/InstanceOverrideSigilReachTests.cs`.

## Test plan

- **Core:** D1's list-of-`ObjectRef` op emission; D2's diff behavior for a source-only excluded field.
- **EditMode (required — adapter behavior):** D1's live array contents after a build; D2 with a
  builder that authors `m_SortingLayer`; D3's byte-equality across an IdentityMap change; D4's
  typed-selector rewire round-trip asserting the emitted source compiles.
- **Live:** D3 through the real menu commands, since the sequence is specifically about what the
  manual path does that auto-sync does not.

## Not in scope

`IdentityNodeIndex` owns the "an IdentityMap entry is a scene node iff Kind is GameObject or
PrefabInstance" rule by convention only. `Kind` is a bare string, a new `== "GameObject"` node filter
would compile, and four of its ten call sites have no dedicated test. Hardening, not a defect; its
own spec if it is worth doing.
