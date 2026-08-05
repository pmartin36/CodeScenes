# Spec 35 — Reference writes, sorting-layer convergence, and cache invalidation

Four defects found while building spec 33, each in a different subsystem and none depending on
another. They are filed here rather than absorbed into 33.

Each was raised as a claim and then adversarially audited against the tree. Where the audit refuted
the original claim and found the real defect underneath, this spec states the audited version. Two
were confirmed BY CONSTRUCTION — the code cannot do anything else — and that reasoning is given so a
future reader can check it without an editor.

## Build order

D1, D2, D3, D4. Execution order only; no item needs another's code.

## D1 — a populated list of scene references writes null slots

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

**BLOCKED: do not build this item.** It needs the (a)/(b) approach decision below, which is the
repo owner's to make. A run over this spec builds D1, D3 and D4 only.

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

**Decide, then build.** This is an approach decision, not a localized edit:
- **(a) Excluded fields are one-way, made loud.** Keep the exclusion; detect a source-authored
  excluded field and report it as an unrepresentable-field warning through spec 33's located report
  channel, so the author is told the line does nothing instead of silently churning.
- **(b) Excluded fields are prunable.** Carry the adapter's excluded-path set into Core so the
  reconciler can emit a removal for a source-only excluded field, and the line disappears on the
  next sync.

(a) is smaller and reuses a channel that now exists. (b) is what a user would probably expect.
Whoever builds this states the choice in the spec before writing code.

**Accept when:** a builder authoring `m_SortingLayer` reaches a fixed point — either the line is
removed (b), or it survives with a located warning and the build stops emitting a plan op for it (a).
A gate test authors the field in builder source; today none does, which is why 580 tests stayed green
over it.

## D3 — the incremental snapshot cache is never invalidated when the IdentityMap changes

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

**Build:** an instance-target reference emitted into a typed selector produces source that compiles —
either the typed surface accepts an instance handle, or an instance target always renders in the
spelling that binds.

**Accept when:** a field authored in the typed spelling and then rewired onto a prefab-instance root
round-trips to source that compiles, and a second sync is a fixed point. `BuilderCompileCheck` stays
green across that sync.

**Also determine:** whether `PropertyOverride.PropertyPath` can carry the `member:` sigil as far as
`SceneBuilder.Core/Reconcile/SourcePatchApplier.Instances.cs`, whose spelling branch would reproduce
this at a second site. If it can, that site is in scope; if not, record why and leave it.

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
