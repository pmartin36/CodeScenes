# Spec 43 — reference forward-declaration and the placement floor

> **STATUS: BLOCKED on an authoring-vocabulary decision (2026-08-14).** A first pipeline run built
> b1-t1 (the generalized max-declaration-index placement floor) GREEN, then b1-t2's research proved
> the spec-compliant fix cannot be built in the current vocabulary and escalated. The floor is
> necessary but inert alone (the in-place `IntroduceComponentField` fold never reaches
> `StatementPlacement`). The remaining shape — a standalone statement that configures an
> ALREADY-authored component with the deferred reference field, without re-adding it, round-tripping
> through `BuilderParser` — has no verb: `Component<T>()` returns a `NodeHandle` you cannot recapture,
> `.Set` exists only inside the configure closure, `ComponentRef<T>` is reference-only, and every
> statement-level `.Component<T>` is a distinct component under ordinal identity (so a second
> `.Component<Linker>(c => c.Set("target", door))` after `door` authors a DUPLICATE). Verified at
> `NodeHandle.cs:101,108`, `ComponentHandle.cs`, `ComponentRef.cs`, `BuilderParser.cs:487-510,762-769`.
> Generalizing the floor and RELOCATING the component past the target is also dead: it reorders
> components against the sibling-ordinal rule and re-churns every sync. So the fix REQUIRES a new
> public authoring verb (Runtime + `BuilderParser` recognition + DocGen + EditMode), which makes this
> a feature, not a localized defect fix. To resume: decide the verb shape (candidate:
> `opener.Set<Linker>("target", door)` as a deferred field-set on component #0, seated after the
> target's `var`), amend the In-scope/Deliverables/Decomposition below to add the verb + its
> round-trip, then re-run the pipeline fresh (an ESCALATED verdict is cached, so relaunch with
> `{ spec }`, not resume-by-id). The reproduced `CS0841` entry stays in `docs/open-defects.md` until
> this ships with its regression test.

One defect, reproduced live over two manual syncs with auto off: a scene reference from an
EARLIER-declared object to a LATER-declared root emits a forward reference, and the written builder
source fails to compile with `CS0841`. This is the root-append variant left OUT OF SCOPE by spec 41
(`specs/completed/41-snapshot-emit-classification.md:72-73`), now reproduced and diagnosed.

CLAUDE.md rates a write path that can emit non-compiling source a bug, not a style issue: "a write
path that can emit non-compiling source is a bug." The emission here is silent. The reconciler
reports zero conflicts and zero edits over the pending field on the first sync, then on the second
sync writes a statement that names a `var` declared on the following line.

## The measured defect

REPRODUCED LIVE (two manual syncs, auto off). Starting state: a converged `Opener` whose builder
reads

```
var opener = scene.Add("Opener");
opener.Component<Linker>();
```

with `Linker.target` unauthored. In the scene, create a new ROOT `Door` at end-of-roots, so the
roots are `Opener@0, Door@1`, and wire `Opener.Linker.target = Door`.

- Sync 1 appends `scene.Add("Door");` after `Opener` and silently omits the pending target (the
  target's `Door` is not yet in the IdentityMap, so it classifies `Pending` and defers, per spec 41's
  report-channel contract, `41-snapshot-emit-classification.md:130`).
- Sync 2 (with `Door` now mapped) emits `opener.Component<Linker>(c => c.Set("target", door));` on
  line 9, BEFORE `var door = scene.Add("Door");` on line 10. The emitted source names `door` above its
  declaration. `SceneBuilderSync.Run(...).CompileErrors` = `CS0841`, raised at
  `com.codescenes/Editor/BuilderCompileCheck.cs:286`.

REFUTED PREMISE (recorded so it is not re-investigated). This is NOT a mis-seated append. The append
is seated at its live sibling index via `AppendStatement.NewSiblingIndex = siblingIndex`
(`SceneBuilder.Core/Reconcile/ReconcilerAppends.cs:203`); reordering `Door` to sibling 0 moves its
declaration to the top and the file compiles. Seating is correct. The defect is elsewhere.

### Real cause, verified against the tree

The reference-introducing patch is folded IN PLACE into the anchor's existing statement, on a path
that never consults statement placement.

- `ComponentReconciler` emits `IntroduceComponentField` for a newly-authored reference field, at both
  introduce branches: `SceneBuilder.Core/Reconcile/ComponentReconciler.cs:468` and `:519`. The
  `Resolvable` branch pre-renders the handle expression (`NewExpr = RenderFieldValue(...)`), which for
  the repro is the target's local `door`.
- `ComponentPatchApplier.ResolveIntroduceComponentField`
  (`SceneBuilder.Core/Reconcile/ComponentPatchApplier.cs:187`) applies it as an in-place rewrite of
  the anchor's existing `.Component<T>(...)` invocation. The applier at `:233-269` grows the anchor
  invocation's closure (`c => ...` gains a `.Set(...)`, or an empty argument list gains a lambda), and
  the invocation node keeps its original block position. It never routes through `StatementPlacement`
  and never checks that a handle named in the introduced value is declared earlier than the anchor
  statement.
- `StatementPlacement.MinIndexAfterReceiverDeclaration`
  (`SceneBuilder.Core/Reconcile/StatementPlacement.cs:133`, consumed at `:244`) would not have caught
  it even if the edit had routed through placement: its floor is the declaration index of the RECEIVER
  only (`opener`), which sits at the top of the block. Handles used as ARGUMENTS (`door`) take no part
  in the floor. `DeclarationIndexOf` (`:113`) is per-handle, but is only ever asked about the receiver.

A scene reorder is NOT a fix. `Door` belongs at its later sibling index; moving it earlier to dodge the
compile error would fight the author's layout and re-churn on the next sync.

The self-reference sibling of this bug is already closed:
`SceneBuilder.Core.Tests/SelfReferenceEmitTests.cs` proves a field whose target is the node the
component sits on emits `NodeHandle.Self` rather than the owner's own local. That is the degenerate case
(`declaration index == statement index`). This spec generalizes the same invariant to
`declaration index > statement index`: a forward reference to any later-declared handle.

## The invariant

Emitted builder source is always compilable. Concretely, for this class of bug:

> No emitted statement may name a handle whose declaration sits at or after the statement's own
> position in the block.

"Names a handle" covers the RECEIVER of the chain and EVERY handle used as an argument (a rendered
`ObjectRef`/`AssetRef` expression, a list element, a selector target). Self-reference (declaration
index equal to statement index) and forward-reference (declaration index greater) are the two failing
cases; both compile to `CS0841`.

### Owner, mechanism, check

**Owner.** `StatementPlacement` (`SceneBuilder.Core/Reconcile/StatementPlacement.cs`) is THE
statement-placement path. Its own contract (`:10-32`) already states RULE 2: "C# requires a local to
be declared before it is used... A statement may not be seated above the declaration of the handle it
CALLS (the FLOOR)." The floor exists; it is under-scoped to the receiver, and the reference-introducing
edit bypasses the path entirely. Both halves are this spec's target. The invariant is a property of the
emitted language, so it belongs on the one path that cannot be bypassed, not re-derived per edit kind.

**Mechanism (two parts that compose).**

1. Generalize the floor. `MinIndexAfterReceiverDeclaration` becomes a max-declaration-index floor over
   ALL handles a statement names, receiver and arguments alike: `1 + max(DeclarationIndexOf(h))` over
   every local `h` the statement references that is declared in this block, or 0 when none is a local
   declared here. This is the single shared rule every current and future placing edit inherits. For a
   statement to be subject to the floor, the placer must know which handles it names; the edit that
   carries a pre-rendered value expression (`IntroduceComponentField.NewExpr`, and the sibling
   `FieldExpressions` channels) must expose the set of handle identifiers that expression uses so the
   floor can consult them. Parsing the identifiers out of the rendered expression at placement time is
   acceptable; carrying them on the edit is cleaner. The blueprint picks one.

2. Route the reference-introducing field-set through that path as a DEFERRED statement, not an in-place
   fold, whenever its value names a handle declared later than the anchor. A folded
   `opener.Component<Linker>(c => c.Set("target", door))` cannot be relocated below `var door` without
   breaking a different invariant (see the RULE 1 / RULE 2 collision below), so the field-set is
   emitted as its own statement seated after the target's declaration by part 1's floor, leaving the
   anchor's `.Component<T>()` statement where it is. This is the "deferred-field-assignment emission
   shape" the defect names.

**Check.** A structural Core assertion, generalizing
`SelfReferenceEmitTests.AssertNoLocalUsedInOwnDeclaration` to the forward case: for every statement at
block index `i`, every in-block local it names has declaration index `< i`. It fails on the un-fixed
tree (the anchor names `door` at a lower index than `door`'s declaration). Paired with a real Roslyn
compile assertion (`AuthoringBindHarness.BindErrors` in Core, `BuilderCompileCheck` /
`EmittedCodeCompiles.SyncAndAssertCompiles` in the gate) so the check proves compilation, not only a
syntactic property. A structural check alone that some future emission shape sidesteps would pass while
the source still fails to compile; the compile assertion is the backstop.

## Why not just relocate the anchor (the RULE 1 / RULE 2 collision)

The obvious cheap fix, generalize the floor and move the anchor `opener.Component<Linker>()` statement
below `var door`, is unsafe and is rejected here.

`StatementPlacement` RULE 1 (`:16-23`) makes a statement's block position its SCENE-GRAPH sibling
index: `BuilderParser` derives a component's ordinal-within-type from the ORDER of the `.Component<T>()`
statements feeding the same receiver. If `opener` has more than one component and the forward-referring
one is not last, sliding it below `var door` slides it past its component peers, so the emission
re-parses to a different component ordinal, and the next sync re-Reorders it. Satisfying RULE 2 by
relocation would violate RULE 1 for any node with multiple components. The deferred separate statement
avoids the collision: it does not feed the receiver's component list (it references an
already-authored component), so relocating it changes no sibling ordering, exactly the property RULE 2's
"move the dependent group" relies on (`:26-32`).

The measured repro has a single component, where naive relocation happens to be order-safe. Fixing only
that case would leave the multi-component case broken and is not spec-compliant.

## Fix direction

The defect entry offers two directions. They are not exclusive; this spec composes them, and the
composition is the recommendation for review:

- (a) deferred-field-assignment emission shape — REQUIRED. It is the only shape that satisfies the
  invariant universally without violating component sibling order. It is mechanism part 2 above.
- (b) generalize the placement floor to the max declaration index over all named handles — REQUIRED as
  mechanism part 1. On its own it is inert for this bug, because the in-place fold never reaches the
  placement path; it is the floor the deferred statement in (a) is seated against, and it hardens every
  other placing edit against the same class of forward-reference.

Open design question for review: the concrete authoring token of the deferred shape. It must not re-add
the component (a second `.Component<Linker>()` statement is a duplicate under ordinal-within-type
identity), and it must round-trip through `BuilderParser`. Candidate: capture the component handle in a
local and set the field on it in a later statement. The exact token is for the research blueprint; the
constraints (no re-add, must round-trip, seated by the generalized floor) are fixed here.

## In scope

- Generalize `StatementPlacement`'s floor to a max-declaration-index over every handle a placed
  statement names, receiver and arguments, at the single placement path.
- Expose, on the reference-introducing edit, the set of handle identifiers its rendered value uses (or
  parse them at placement time), so the floor can see argument handles.
- Emit the reference-introducing field-set as a deferred statement routed through statement placement,
  rather than an in-place closure fold, whenever its value names a handle declared later than the
  anchor. Preserve the in-place fold for the ordinary case where every named handle is already declared
  earlier (the common path, unchanged).
- The structural forward-reference check plus a Roslyn compile assertion, driven through the live
  two-sync sequence, not one trusted site.

## Out of scope

- The `Pending`-then-converge behavior of sync 1. Omitting the still-unmapped target on the first sync
  is correct (spec 41 report-channel contract); the bug is sync 2's placement, not sync 1's deferral.
- List-of-reference and asset-reference forward declarations beyond what the generalized floor already
  covers by naming every argument handle. The floor is general by construction; the tests cover the
  scalar `ObjectRef` repro and one list case, and no new value type is introduced.
- The other open defects sharing `FOUND-BY: reference-writes-and-cache-invalidation` (double-emit,
  cold-assemble perf, uncached ref resolver, per-keystroke skipped-field logging). Unrelated subsystems.
- Performance of the placement pass. The floor computation is O(statements × named handles) per placed
  statement, no new whole-scene walk.

## Additions to the contract

No new Core value type and no new `ConflictKind`. The generalized floor is a change to an existing
private helper on `StatementPlacement`. The deferred-assignment shape is one new `SourceEdit` emission
shape (or an existing introduce edit gaining a "seat as its own statement" mode) plus its applier; the
blueprint decides whether it is a new record or a mode flag on `IntroduceComponentField`. Whichever it
is, its placement goes through `PlaceNewStatement` / `PlacementIndex`, never an in-place rewrite, so it
inherits the floor.

## Core deliverables

- `StatementPlacement.MinIndexAfterReceiverDeclaration` (`SceneBuilder.Core/Reconcile/StatementPlacement.cs:133`)
  generalizes to a floor over ALL handles a statement names, consumed by `PlacementIndex` at `:244`.
  Every placement-routed edit inherits it. A helper enumerates the in-block local handles a statement
  references (receiver via `RootReceiverName`, arguments via the rendered expression's identifiers).
- The reference-introducing field-set, when its value forward-references, is emitted as a deferred
  statement seated by that floor after the target's declaration, at
  `SceneBuilder.Core/Reconcile/ComponentReconciler.cs:468`/`:519` (emit) and its applier (place, do not
  fold in place). The in-place fold at `ComponentPatchApplier.cs:233` stays the path for the
  no-forward-reference case.
- The structural forward-reference check (generalizing
  `SceneBuilder.Core.Tests/SelfReferenceEmitTests.cs`): no statement names an in-block local declared
  at or after its own position, asserted over the applied source of the two-pass simulation.
- A headless two-pass simulation of the live repro (`Opener` with `Linker.target` unauthored, `Door`
  appended as a later root, target wired on the second pass) whose applied source is compiled by
  `AuthoringBindHarness.BindErrors` and asserted to produce zero errors (specifically no `CS0841`).

## Editor adapter deliverables

The read path that produces the forward-reference is Unity-observable: the target is a scene ROOT
created after the referrer, and the reference is wired in a live scene across two manual syncs. That
sequence is not reachable from a POCO fixture, so it needs an EditMode test in
`unity-gate/Assets/GateTests/` that reproduces it against a real editor scene and asserts the emitted
source compiles. Per the CLAUDE.md hard requirement, a Unity-observable behavior is not complete
without EditMode coverage that exercises the real boundary; the headless Core simulation proves the
placement half but is structurally blind to the real `SceneBuilderSync.Run` compile check.

## Decomposition guidance

Hoist the shared placement rule; do not restate it per task.

- Task A (the shared floor, its own owning task). Generalize the `StatementPlacement` floor to the
  max-declaration-index over all named handles, add the handle-enumeration helper, and add the
  structural forward-reference check. This is the one mechanism every placing edit inherits; it owns the
  check. TOUCHES: `SceneBuilder.Core/Reconcile/StatementPlacement.cs`,
  `SceneBuilder.Core.Tests/**` (the structural check and the placement unit tests). Depends on nothing.
- Task B (the deferred emission shape). Emit the reference-introducing field-set as a deferred,
  placement-routed statement when its value forward-references, and apply it through `PlaceNewStatement`
  rather than the in-place fold. Reuses Task A's floor. TOUCHES:
  `SceneBuilder.Core/Reconcile/ComponentReconciler.cs`,
  `SceneBuilder.Core/Reconcile/ComponentPatchApplier.cs`,
  `SceneBuilder.Core/Reconcile/SourceEdit.cs` (if a new record or mode flag is added),
  `SceneBuilder.Core.Tests/**` (the two-pass compile simulation).
- Task C (the live EditMode repro). The two-sync gate test whose reference target is a scene ROOT.
  Depends on B. TOUCHES: `unity-gate/Assets/GateTests/**`.

Keep each task's TOUCHES complete: a task that omits a file it edits mis-scopes the gate. Tasks A and B
are Core-only and fast-gate; Task C touches `unity-gate/` and takes the full gate.

## Core and adapter test plan

RED tests, behavior not structure:

- Structural (Core, Task A). Over an applied source where a statement forward-references a
  later-declared local, the generalized check fails on the un-fixed tree and passes on the fixed one.
  Drive it through the two-pass simulation output, not a hand-written string.
- Two-pass compile simulation (Core, Task B). Simulate the live sequence: pass 1 appends `Door` as a
  later root and defers the pending target; pass 2 wires `Opener.Linker.target = Door`. Assert the
  applied source, compiled through `AuthoringBindHarness.BindErrors`, yields zero errors and no
  `CS0841`, and that a third pass over the converged source produces zero edits (fixed point).
- Live two-sync (EditMode, required, Task C). In a real scene, converge an `Opener` with an unauthored
  `Linker.target`, create a new ROOT `Door` at end-of-roots, wire `Opener.Linker.target = Door`, and
  run two manual syncs through `EmittedCodeCompiles.SyncAndAssertCompiles`. Assert the second sync's
  builder source compiles (`BuilderCompileCheck` stays green, no `CS0841` logged) and the reference
  round-trips, and a third sync is a fixed point.

## Unity confirmation checklist

These become the EditMode tests above.

1. Converge a scene with one root `Opener` carrying a `Linker` component whose reference field is
   unauthored. Add a new root object `Door` to the scene, positioned after `Opener`. Set
   `Opener`'s `Linker.target` to `Door`. Sync once, then sync again. Expected: the builder source
   compiles after both syncs; `Opener.Linker.target` is authored as a handle to `Door`; a third sync
   produces zero edits.
2. Repeat item 1 with `Opener` carrying two components (say `Linker` then a second behavior), the
   forward-referring `Linker` authored first. Sync twice. Expected: the source compiles AND the two
   components keep their authored order (no component reorder churn on the following sync).
3. Repeat item 1 where `Linker.target` is a `List<GameObject>` holding one earlier-declared object and
   the new later-declared `Door`. Sync twice. Expected: the list authors both elements, the source
   compiles, and a third sync is a fixed point.

## Dependencies

- M2 reconcile (`SourceEdit`, `SourcePatchApplier`, `StatementPlacement`).
- M4/M5 references (`ObjectRef` and its handle rendering, `RenderFieldValue`).
- Spec 41 (`specs/completed/41-snapshot-emit-classification.md`): the `Pending`-defer contract that
  produces sync 1's silent omission, and the classification this spec's emit sites already route
  through. This spec is the root-append variant 41 explicitly deferred (`41:72-73`).
- `SelfReferenceEmitTests`: the self-reference degenerate case this spec generalizes.

## Risks and notes

- The generalized floor must enumerate argument handles from the RENDERED value expression, whose
  identifiers are the target locals. A naive identifier scan could over-count (an identifier that is a
  type name or a member access, not a local). Scope the scan to identifiers that match an in-block
  local declaration, which `DeclarationIndexOf` already resolves; a non-local identifier resolves to -1
  and contributes nothing to the floor.
- The deferred shape must round-trip: `BuilderParser` has to read it back into the same model the folded
  form produces, or the next sync sees a phantom diff. The two-pass simulation's fixed-point assertion
  (third pass, zero edits) is what proves this; it is not optional.
- Keep the in-place fold for the common no-forward-reference case. Switching every reference field to a
  deferred statement would churn source that compiles today. The deferred shape is conditional on the
  value naming a later-declared handle.
