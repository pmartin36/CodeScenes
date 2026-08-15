# Spec 43: reference forward-declaration and the declare-before-use rule

One defect, reproduced live over two manual syncs with auto off: a component field that references a
LATER-declared handle emits the folded reference ABOVE the target's declaration, so the written
builder source fails to compile with `CS0841`. This is the root-append variant spec 41 left out of
scope (`specs/completed/41-snapshot-emit-classification.md:72-73`), now reproduced and diagnosed. The
fix emits the reference TARGET before the reference-bearing component, so the reference folds inside
the normal `.Component<T>(c => c.Set(...))` closure and compiles. It adds NO authoring verb, changes
NO return type, touches NO Runtime or DocGen surface, and emits today's exact syntax. It is entirely a
reconcile/emit-layer change: only the ORDER in which statements are emitted and placed changes.

CLAUDE.md rates a write path that can emit non-compiling source a bug, not a style issue: "a write
path that can emit non-compiling source is a bug." The emission here is silent. The reconciler reports
zero conflicts and zero edits over the pending field on the first sync, then on the second sync writes
a statement that names a `var` declared on a later line.

## The measured defect

REPRODUCED LIVE (two manual syncs, auto off). Starting state: a converged `Opener` whose builder
reads

```
var opener = scene.Add("Opener");
opener.Component<Linker>();
```

with `Linker.target` unauthored. In the scene, create a new ROOT `Door` at end-of-roots, so the roots
are `Opener@0, Door@1`, and wire `Opener.Linker.target = Door`.

- Sync 1 appends `scene.Add("Door");` after `Opener` and silently omits the pending target (the
  target's `Door` is not yet in the IdentityMap, so it classifies `Pending` and defers, per spec 41's
  report-channel contract, `41-snapshot-emit-classification.md:130`).
- Sync 2 (with `Door` now mapped) folds the reference into the anchor's existing statement, emitting
  `opener.Component<Linker>(c => c.Set("target", door));` on line 2, ABOVE `var door = scene.Add("Door");`
  further down the block. The emitted source names `door` before its declaration.
  `SceneBuilderSync.Run(...).CompileErrors` = `CS0841`, surfaced at
  `com.codescenes/Editor/BuilderCompileCheck.cs:286` (`CS0841` is called out in that file's own
  contract, `:36`).

The common shape of the same defect is a parent whose own component references its own child. Authored
naturally the child is declared first and the reference folds cleanly:

```
var parent = scene.Add("parent");
var child  = parent.Add("child");                       // target declared first
parent.Component<Aim>(c => c.Set("muzzle", child));     // folds; child already in scope
```

The reconciler, emitting the same scene from scratch, gets the order backwards: it emits the parent's
components before it recurses into the parent's children, so the referencing component is written above
the child's `Add`.

### Real cause, verified against the tree

Two emit sites produce the forward reference. The measured repro is the second.

**The in-place introduce fold.** On the introduce path,
`ComponentReconciler` emits `IntroduceComponentField` for a newly-authored reference field, at both
introduce branches (`SceneBuilder.Core/Reconcile/ComponentReconciler.cs:478` and `:529`). Each
pre-renders the handle expression when the target is resolvable
(`NewExpr = RenderFieldValue(...)` at `:483` and `:534`), which for the repro is the target's local
`door`. `ComponentPatchApplier.ResolveIntroduceComponentField`
(`SceneBuilder.Core/Reconcile/ComponentPatchApplier.cs:187`) applies it as an in-place rewrite of the
anchor's existing `.Component<T>(...)` invocation. The applier at `:233-269` grows the anchor
invocation's closure (`c => ...` gains a `.Set(...)`, or an empty argument list gains a lambda); the
invocation node keeps its original block position. It never routes through `StatementPlacement` and
never checks that a handle named in the introduced value is declared earlier than the anchor statement.

**The receiver-only floor.** `StatementPlacement.MinIndexAfterReceiverDeclaration`
(`SceneBuilder.Core/Reconcile/StatementPlacement.cs:133`, consumed by `PlacementIndex` at `:244`) would
not have caught the fold even had it routed through placement: its floor is the declaration index of
the RECEIVER only (`opener`, at the top of the block). Handles used as ARGUMENTS (`door`) take no part
in the floor. `DeclarationIndexOf` (`:113`) is per-handle, but is only ever asked about the receiver.

**The from-scratch emit order.** `Reconciler.DetectAppends`
(`SceneBuilder.Core/Reconcile/ReconcilerAppends.cs`) emits the parent `AppendStatement` (`:199`), then
the parent's component appends (the `EmitComponentAppend` loop, `:229-257`), then recurses into the
parent's children (`:259`). Components are emitted BEFORE children. A parent component that references a
same-batch child is therefore written above the child's `Add`.

The self-reference sibling of this bug is already closed:
`SceneBuilder.Core.Tests/SelfReferenceEmitTests.cs` proves a field whose target is the node the
component sits on emits `NodeHandle.Self` rather than the owner's own local. That is the degenerate case
(`declaration index == statement index`). This spec generalizes the same invariant to
`declaration index > statement index`: a reference to any later-declared handle.

## Why declare-before-use converges without churn (verified empirically this session)

Emitting the target before the referrer does not perturb the scene graph, and the fixed point is
reachable. Three verified facts carry the design.

- A node's COMPONENTS and its CHILDREN live in separate, independently-ordered peer lists
  (`SceneBuilder.Core/Reconcile/StatementPlacement.cs:35-37`, `PeerKind.Component` versus
  `PeerKind.Child`). A sibling index is only meaningful against same-kind peers. Moving a child-`Add`
  relative to a component, or a component relative to a child-`Add`, changes neither list's ordering.
- The component REORDER pass (`SceneBuilder.Core/Reconcile/ComponentReconciler.cs:200-232`) builds
  `sourceIndexByKey` over component-only arrays (`ExcludeTransform(...)` at `:109`, keyed by
  `(TypeFullName, Ordinal)` at `:110`) and emits a `ReorderStatement` (`:229`) ONLY when a component's
  index AMONG COMPONENTS differs. Children never enter this comparison. So moving a child-`Add` ahead of
  a component, or a component past a child-`Add`, triggers no reorder churn.
- Empirically (`Reconciler.Reconcile`, one snapshot of parent + a component whose field references its
  child): the child-before-component form reconciles to `edits=0 reorder=0 conflicts=0` AND compiles;
  the component-before-child form (the current emission) also reports `edits=0` but fails Roslyn compile
  with `CS0841`. Child-before-component is a compiling fixed point; the emit order is the whole defect.

This is why declare-before-use differs from relocating a component past another component: here a
node-declaration and a component are DIFFERENT peer kinds, so bringing them into declare-before-use
order never moves a component past a same-kind peer and never re-parses to a new component ordinal.

## The invariant

Emitted builder source is always compilable. Concretely, for this class of bug:

> No emitted statement may name a handle whose declaration sits at or after the statement's own
> position in the block.

"Names a handle" covers the RECEIVER of the chain and EVERY handle used as an argument (a rendered
`ObjectRef`/`AssetRef` expression, a list element, a selector target). Self-reference (declaration
index equal to statement index) and forward-reference (declaration index greater) are the two failing
cases; both compile to `CS0841`. The fix satisfies the invariant by ORDER: the target's declaration is
emitted or seated before every statement that names it.

### Owner, mechanism, check

**Owner.** `StatementPlacement` (`SceneBuilder.Core/Reconcile/StatementPlacement.cs`) is THE
statement-placement path. Its own contract (`:25-32`, RULE 2) already states that a statement may not be
seated above the declaration of the handle it CALLS (the FLOOR), and that a statement declaring a handle
may not be seated below the statements that call it (the CEILING). The floor exists but is under-scoped
to the receiver, and the reference-introducing fold bypasses the path entirely. Both halves are this
spec's target. The invariant is a property of the emitted language, so it belongs on the one path that
cannot be bypassed, not re-derived per edit kind.

**Mechanism.** Two owned pieces, both order-only:

1. **The generalized placement floor (the un-bypassable seat).** `MinIndexAfterReceiverDeclaration`
   becomes a max-declaration-index floor over ALL handles a statement names, receiver and arguments
   alike: `1 + max(DeclarationIndexOf(h))` over every local `h` the statement references that is
   declared in this block, or 0 when none is. For the floor to see argument handles, the placer must
   know which handles a statement names; a placed statement carrying a pre-rendered value expression
   (`IntroduceComponentField.NewExpr`) exposes the handle identifiers that expression uses, or the placer
   parses them from the rendered expression at placement time. The blueprint picks one. This is the
   single rule every current and future placing edit inherits, and it is the CEILING's symmetric twin:
   a declaration newly depended upon is hoisted above its new dependent, the same way RULE 2 already
   moves a dependent group below its declaration.

2. **The emit and placement order.** On the from-scratch path, `DetectAppends` emits a referenced
   same-batch child's `Add` BEFORE the referencing component and gives that child its OWN handle, so the
   target resolves as a same-batch resolvable target rather than deferring to a second sync. On the
   introduce path, a reference-bearing field whose value names a later-declared handle routes THROUGH
   placement rather than the in-place fold: the target handle's declaration is hoisted to the lowest
   block index that is still at or after its own earlier same-kind peers (RULE 1) and strictly before
   the referencing component (the reference-imposed ceiling), after which the reference folds into the
   anchor's existing closure and compiles. Because the target's declaration is a child/root `Add` and the
   referencing statement is a `Component`, this hoist crosses only different-peer-kind statements and
   perturbs no component ordinal (the reorder pass keys on components only). The anchor `.Component<T>()`
   statement does not move, so no node with any number of components can churn.

**Check.** A structural Core assertion, generalizing
`SelfReferenceEmitTests.AssertNoLocalUsedInOwnDeclaration`
(`SceneBuilder.Core.Tests/SelfReferenceEmitTests.cs:48`) from the self case to the forward case: for
every statement at block index `i`, every in-block local it names has declaration index `< i`. It fails
on the un-fixed tree (the anchor names `door` above `door`'s declaration) and passes after. Paired with
a real Roslyn compile assertion (`AuthoringBindHarness.BindErrors`,
`SceneBuilder.Core.Tests/AuthoringBindHarness.cs:110`, in Core;
`EmittedCodeCompiles.SyncAndAssertCompiles`, `unity-gate/Assets/GateTests/EmittedCodeCompiles.cs:46`, in
the gate) so the check proves compilation, not only a syntactic property. A structural check alone that
some future emission shape sidesteps would pass while the source still fails to compile; the compile
assertion is the backstop.

## The decided design: declare-before-use

The reconciler emits the reference TARGET before the reference-bearing component. The reference then
folds inside the ordinary `.Component<T>(c => c.Set(...))` closure, exactly as an author or an LLM would
write it, and compiles. No new vocabulary, no return-type change, no separate configuration statement.
This matches how a human or an LLM naturally authors and fixes: declare, then wire. The two emit paths
that produce a forward reference are fixed at their sources.

### The two code paths the fix must cover

1. **From-scratch create path** (`SceneBuilder.Core/Reconcile/ReconcilerAppends.cs`, `DetectAppends`).
   Today the parent `AppendStatement` (`:199`) is emitted, then the parent's components
   (`EmitComponentAppend` loop, `:229-257`), then the children (recursion, `:259`), so a parent's
   component that references its own child is written above the child's `Add`. The same path also drops a
   cross-reference between two same-batch nodes as a pending create-candidate rather than resolving it in
   this sync, and mis-names the handle it does emit (see "Also in scope"). The fix: when a node's
   component references a same-batch child (or other same-batch node), emit that referenced node's `Add`
   BEFORE the referencing component, and give that node its OWN handle (`headsHandle`, `:163-166`,
   currently gates a handle on having create-candidate children, representable components, or a duplicate
   name; being a same-batch reference target is added as a fourth reason) so the target resolves as a
   same-batch resolvable target instead of deferring to a second sync.
2. **Introduce path** (the live two-sync repro). A reference field added to an already-converged scene.
   When the folded value names a later-declared handle, the edit routes through placement rather than the
   in-place fold at `ComponentPatchApplier.ResolveIntroduceComponentField` (`:187`, `:233-269`): the
   generalized floor hoists the target handle's declaration above the referencing component, staying
   after the target's own earlier same-kind peers, and the reference then folds cleanly. The hoist is
   churn-free because a node-declaration is not a component peer. The anchor component does not move. When
   the folded value names only handles already declared earlier, the in-place fold stays the path
   unchanged, so existing generated files do not churn.

### Rejected alternatives

Recorded so a future reader does not re-propose them.

- **Relocate the referencing component below the target.** Churns on the component reorder pass
  (`ComponentReconciler.cs:200-232`). Sliding a forward-referring component below the target moves it past
  its same-type component peers whenever it is not the last component on its owner, so the emission
  re-parses to a different component ordinal and the next sync re-Reorders it. Safe only for the
  single-component case; broken for the general one.
- **A capturable `ComponentHandle<T>` returned from `Component<T>()`.** Changing the return type of
  `NodeHandle.Component<T>()` breaks fluent chaining. The corpus chains `.Transform` / `.FitSize` / `.Ref`
  / a further `.Component` off the result and captures the whole chain as a `SceneObjectHandle`. It also
  drags in Runtime, parser, and DocGen changes for a defect that is entirely reconcile-internal.
- **A `Configure<T>(ordinal)` deferred-set verb.** A codegen-only construct no human or LLM would author
  from scratch (they hit `CS0841` and simply reorder), plus a fragile source ordinal that goes stale the
  moment a duplicate same-type component is deleted or reordered.

Declare-before-use was chosen because it adds no vocabulary, keeps the emitted syntax identical to what a
human writes, and fixes the defect the same way a human would.

## In scope

- Generalize `StatementPlacement`'s floor to a max-declaration-index over every handle a placed
  statement names, receiver and arguments, at the single placement path; expose, on the
  reference-introducing edit, the set of handle identifiers its rendered value uses (or parse them at
  placement time) so the floor can see argument handles.
- Fix the from-scratch emit order in `DetectAppends`: a referenced same-batch node's `Add` is emitted
  before the referencing component, that node acquires its own handle, and the cross-reference resolves
  same-batch instead of deferring, with correct handle names (no `CS0103`).
- Route a forward-referencing field introduction through placement instead of the in-place fold, so the
  target's declaration is hoisted above the referencing component; keep the in-place fold for the
  ordinary case where every named handle is already declared earlier.
- The structural forward-reference check plus a Roslyn compile assertion, driven through the live
  two-sync sequence, not one trusted site.

## Out of scope

- The `Pending`-then-converge behavior of sync 1. Omitting the still-unmapped target on the first sync is
  correct (spec 41 report-channel contract); the bug is the emit order, not sync 1's deferral.
- Any Runtime or authoring-surface change. The emitted syntax is today's `.Component<T>(c => c.Set(...))`
  closure and today's `var child = parent.Add(...)`, both already authored, parsed, and round-tripped.
  There is NO Runtime deliverable and NO DocGen deliverable.
- Any `BuilderParser` change. Declare-before-use emits only statement shapes the parser already reads.
- Forward references into a prefab instance's hierarchy. A reference to an object inside an instance has
  no IdentityMap node entry and is omitted, not forward-referenced. If reproduced, it is a separate item.
- Performance of the placement pass. The floor computation is O(statements x named handles) per placed
  statement, no new whole-scene walk.

## Also in scope (found by verification)

The from-scratch multi-node cross-reference path has a second defect on the same code. When one
same-batch node's component references another same-batch node, `DetectAppends` currently drops the
cross-reference as a pending create-candidate (deferring it to a later sync) AND emits an incorrect
handle for the referenced node, producing `CS0103` (an undeclared `parent2`-style identifier). The fix
touches this exact path (`ReconcilerAppends.cs`): the referenced node acquires its own handle, the
reference resolves same-batch, and the emitted handle names are correct. It is covered by its own test.

## Additions to the contract

The generalized floor is a change to an existing private helper on `StatementPlacement`; no new Core
value type and no new `ConflictKind`. The introduce-path fix is a routing change: a forward-referencing
`IntroduceComponentField` (`SceneBuilder.Core/Reconcile/SourceEdit.cs:249`) seats through
`PlaceExistingStatement` / `PlacementIndex` rather than the in-place applier, reusing the existing
declaration-hoist machinery (the `MoveStatement` placement path, `SourceEdit.cs:18`, or a sibling; the
blueprint decides whether it is a new edit record or a mode flag on the existing one). Whichever it is,
its placement goes through the one placement path, so it inherits the floor. There is no public-surface
change.

## Core deliverables

- `StatementPlacement.MinIndexAfterReceiverDeclaration`
  (`SceneBuilder.Core/Reconcile/StatementPlacement.cs:133`) generalizes to a floor over ALL handles a
  statement names, consumed by `PlacementIndex` at `:244`. Every placement-routed edit inherits it. A
  helper enumerates the in-block local handles a statement references (receiver via `RootReceiverName` at
  `:86`, arguments via the rendered expression's identifiers), reusing `DeclarationIndexOf` (`:113`).
- `DetectAppends` (`SceneBuilder.Core/Reconcile/ReconcilerAppends.cs`) emits a referenced same-batch
  node's `Add` before the referencing component and gives that node its own handle (extending the
  `headsHandle` decision at `:163-166`), so the from-scratch reference resolves in this sync with a
  correct handle name and no `CS0103`.
- The reference-introducing field-set, when its value forward-references, is seated through
  `PlaceExistingStatement` so the generalized floor hoists the target's declaration above the referencing
  component; the anchor's `.Component<T>()` statement does not move and the reference folds into its
  existing closure. The in-place fold at `ComponentPatchApplier.ResolveIntroduceComponentField`
  (`:187`, `:233-269`) stays the path for the no-forward-reference case.
- The structural forward-reference check (generalizing
  `SceneBuilder.Core.Tests/SelfReferenceEmitTests.cs:48`): no statement names an in-block local declared
  at or after its own position, asserted over the applied source of the two-pass simulation.
- A headless two-pass simulation of the live repro (`Opener` with `Linker.target` unauthored, `Door`
  appended as a later root, target wired on the second pass) whose applied source is compiled by
  `AuthoringBindHarness.BindErrors` and asserted to produce zero errors (specifically no `CS0841`), and
  whose third pass over the converged source produces zero edits (fixed point).

## Editor adapter deliverables

The read path that produces the forward reference is Unity-observable: the target is a scene ROOT created
after the referrer, and the reference is wired in a live scene across two manual syncs. That sequence is
not reachable from a POCO fixture, so it needs an EditMode test in `unity-gate/Assets/GateTests/` that
reproduces it against a real editor scene and asserts the emitted source compiles. Per the CLAUDE.md hard
requirement, a Unity-observable behavior is not complete without EditMode coverage that exercises the
real boundary; the headless Core simulation proves the placement and emit halves but is structurally
blind to the real `SceneBuilderSync.Run` compile check (`EmittedCodeCompiles.SyncAndAssertCompiles`).

## Decomposition

Dependency-ordered. Every task keeps a complete `TOUCHES` list; a task that omits a file it edits
mis-scopes the gate. Only Task D touches `unity-gate/`, so only Task D takes the FULL Unity gate; Tasks
A, B and C are Core-only and fast-gate.

- **Task A: the generalized floor and its check [Core, fast-gate].** Generalize the `StatementPlacement`
  floor to the max-declaration-index over all named handles, add the handle-enumeration helper, and add
  the structural forward-reference check. This is the one placement mechanism every placing edit inherits;
  it owns the check. Depends on nothing. TOUCHES: `SceneBuilder.Core/Reconcile/StatementPlacement.cs`,
  `SceneBuilder.Core.Tests/**`.
- **Task B: from-scratch emit order [Core, fast-gate].** In `DetectAppends`, emit a referenced
  same-batch node's `Add` before the referencing component, give that node its own handle, resolve the
  cross-reference same-batch, and emit correct handle names (fixing the `CS0103` / dropped-reference
  defect). Seated by Task A's floor as a backstop. Depends on A. TOUCHES:
  `SceneBuilder.Core/Reconcile/ReconcilerAppends.cs`,
  `SceneBuilder.Core/Reconcile/ComponentReconciler.cs` (if `EmitComponentAppend`'s same-batch resolvable
  set is threaded differently), `SceneBuilder.Core.Tests/**`.
- **Task C: introduce path routes through placement [Core, fast-gate].** When a field introduction's
  value forward-references, seat it through `PlaceExistingStatement` so Task A's floor hoists the target's
  declaration above the referencing component; keep the in-place fold otherwise. Depends on A. TOUCHES:
  `SceneBuilder.Core/Reconcile/ComponentReconciler.cs`,
  `SceneBuilder.Core/Reconcile/ComponentPatchApplier.cs`,
  `SceneBuilder.Core/Reconcile/SourceEdit.cs` (if a new edit record or mode flag is added),
  `SceneBuilder.Core.Tests/**` (the two-pass compile simulation).
- **Task D: live EditMode two-sync gate test [adapter, FULL gate].** In a real scene, drive the two-sync
  repro with a later-declared scene-ROOT target, and a parent-references-own-child variant, asserting the
  emitted source compiles and a third sync is a fixed point. Depends on B and C. TOUCHES:
  `unity-gate/Assets/GateTests/**`.

## Core and adapter test plan

RED tests, behavior not structure:

- **Task A (Core).** Over an applied source where a statement forward-references a later-declared local,
  the generalized floor seats the dependency in declare-before-use order and the structural check fails on
  the un-fixed tree, passes on the fixed one. Drive it through applied output, not a hand-written string.
- **Task B (Core).** Reconcile from scratch a parent whose component references its own child: assert the
  child's `Add` is emitted above the referencing component, the child heads its own handle, and the
  applied source compiles through `AuthoringBindHarness.BindErrors` (no `CS0841`). A two-node cross-
  reference case asserts the referenced node acquires a handle, the reference resolves in one sync, and the
  emitted handle names compile (no `CS0103`). A second reconcile pass is a fixed point (zero edits).
- **Task C (Core).** Simulate the live sequence: pass 1 appends `Door` as a later root and defers the
  pending target; pass 2 wires `Opener.Linker.target = Door`. Assert the applied source, compiled through
  `AuthoringBindHarness.BindErrors`, yields zero errors and no `CS0841`; the target's declaration is seated
  above the referencing component; the anchor `.Component<Linker>()` did not move; and a third pass is a
  fixed point. A two-same-type-component variant (the forward-referrer authored first) asserts no component
  reorder churn. A no-forward-reference control keeps the in-place closure fold.
- **Task D (EditMode, required).** In a real scene, converge an `Opener` with an unauthored
  `Linker.target`, create a new ROOT `Door` at end-of-roots, wire `Opener.Linker.target = Door`, and run
  two manual syncs through `EmittedCodeCompiles.SyncAndAssertCompiles`. Assert the second sync's builder
  source compiles (`BuilderCompileCheck` stays green, no `CS0841`), the reference round-trips, and a third
  sync is a fixed point. Repeat with a parent whose own component references its own child, created from
  scratch in one sync.

## Unity confirmation checklist

These become the EditMode tests above.

1. Converge a scene with one root `Opener` carrying a `Linker` component whose reference field is
   unauthored. Add a new root object `Door`, positioned after `Opener`. Set `Opener`'s `Linker.target` to
   `Door`. Sync once, then sync again. Expected: the builder source compiles after both syncs; `Door`'s
   declaration is seated above `opener.Component<Linker>(c => c.Set("target", door))`; the roots stay
   `Opener@0, Door@1`; `Opener.Linker.target` resolves to `Door`; a third sync produces zero edits.
2. Author from scratch a parent whose own component references its own child (an `Aim` component on the
   parent whose `muzzle` targets the child). Sync once. Expected: the source compiles in a single sync;
   the child's `Add` is emitted above the referencing component; the child heads its own handle; the
   reference resolves in this sync (no `CS0103`, no deferral); a second sync is a fixed point.
3. Repeat item 1 with `Opener` carrying two `Linker` components, the forward-referring one authored first.
   Sync twice. Expected: the source compiles; the two components keep their authored order (no component
   reorder churn on the following sync); a third sync is a fixed point.

## Dependencies

- M2 reconcile (`SourceEdit`, `SourcePatchApplier`, `StatementPlacement`).
- M4/M5 references (`ObjectRef` and its handle rendering, `RenderFieldValue`).
- Spec 41 (`specs/completed/41-snapshot-emit-classification.md`): the `Pending`-defer contract that
  produces sync 1's silent omission. This spec is the root-append variant 41 explicitly deferred
  (`41:72-73`).
- Spec 35 D4 (`specs/completed/35-reference-writes-and-cache-invalidation.md`): the append path's fixed
  string spelling the folded `.Set` reuses.
- `SelfReferenceEmitTests`: the self-reference degenerate case this spec generalizes.

## Risks and notes

- The generalized floor must enumerate argument handles from the RENDERED value expression, whose
  identifiers are the target locals. A naive identifier scan could over-count (a type name or a member
  access, not a local). Scope the scan to identifiers that match an in-block local declaration, which
  `DeclarationIndexOf` already resolves; a non-local identifier resolves to -1 and contributes nothing.
- The introduce-path hoist must respect the target's own same-kind sibling order (RULE 1) as well as the
  reference-imposed ceiling. Seating a root target too high (before an earlier root peer) would churn root
  order on the next sync; seating it too low (below the referencing component) reproduces the bug. The
  two-pass fixed-point assertion (third pass, zero edits) is what proves both, and it is not optional.
- Keep the in-place fold for the common no-forward-reference case. Routing every reference field through
  placement would churn source that compiles today. The placement route is conditional on the value
  naming a later-declared handle.
- The from-scratch and introduce paths must agree on the emitted shape, or a scene created from scratch
  and a scene converged over two syncs would diverge and the next sync would see a phantom diff. Both emit
  the same `var target = ...; owner.Component<T>(c => c.Set(field, target));` order.
