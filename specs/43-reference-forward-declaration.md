# Spec 43 — reference forward-declaration and the placement floor

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
  declaration. `SceneBuilderSync.Run(...).CompileErrors` = `CS0841`, surfaced at
  `com.codescenes/Editor/BuilderCompileCheck.cs:286`.

REFUTED PREMISE (recorded so it is not re-investigated). This is NOT a mis-seated append. The append
is seated at its live sibling index via `AppendStatement.NewSiblingIndex = siblingIndex`
(`SceneBuilder.Core/Reconcile/ReconcilerAppends.cs:203`); reordering `Door` to sibling 0 moves its
declaration to the top and the file compiles. Seating is correct. The defect is elsewhere.

### Real cause, verified against the tree

The reference-introducing patch is folded IN PLACE into the anchor's existing statement, on a path
that never consults statement placement.

- `ComponentReconciler` emits `IntroduceComponentField` for a newly-authored reference field, at both
  introduce branches: `SceneBuilder.Core/Reconcile/ComponentReconciler.cs:478` and `:529`. The
  `Resolvable` branch pre-renders the handle expression (`NewExpr = RenderFieldValue(...)` at `:483`
  and `:534`), which for the repro is the target's local `door`.
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

**Two owned mechanisms.** A forward reference has exactly one way to be expressed once both land:

1. **The generalized placement floor (the un-bypassable seat).** `MinIndexAfterReceiverDeclaration`
   becomes a max-declaration-index floor over ALL handles a statement names, receiver and arguments
   alike: `1 + max(DeclarationIndexOf(h))` over every local `h` the statement references that is
   declared in this block, or 0 when none is a local declared here. This is the single shared rule
   every current and future placing edit inherits. For a statement to be subject to the floor, the
   placer must know which handles it names; the edit that carries a pre-rendered value expression
   (`IntroduceComponentField.NewExpr`, and the sibling deferred-set channel introduced by mechanism 2)
   must expose the set of handle identifiers that expression uses so the floor can consult them.
   Parsing the identifiers out of the rendered expression at placement time is acceptable; carrying
   them on the edit is cleaner. The blueprint picks one.

2. **The deferred-config authoring shape (the one way to express a forward reference).** A capturable
   component handle plus its deferred `.Set`, spelled out under "The decided design" below. A folded
   `opener.Component<Linker>(c => c.Set("target", door))` cannot be relocated below `var door` without
   breaking a different invariant (see the RULE 1 / RULE 2 collision below), so the field-set is
   emitted as its OWN statement on a captured handle, seated after the target's declaration by
   mechanism 1's floor, leaving the anchor's `.Component<T>()` statement where it is.

**Check.** A structural Core assertion, generalizing
`SelfReferenceEmitTests.AssertNoLocalUsedInOwnDeclaration` (`SceneBuilder.Core.Tests/SelfReferenceEmitTests.cs:48`)
to the forward case: for every statement at block index `i`, every in-block local it names has
declaration index `< i`. It fails on the un-fixed tree (the anchor names `door` at a lower index than
`door`'s declaration). Paired with a real Roslyn compile assertion (`AuthoringBindHarness.BindErrors`
in Core, `BuilderCompileCheck` / `EmittedCodeCompiles.SyncAndAssertCompiles` in the gate) so the check
proves compilation, not only a syntactic property. A structural check alone that some future emission
shape sidesteps would pass while the source still fails to compile; the compile assertion is the
backstop.

## Why not just relocate the anchor (the RULE 1 / RULE 2 collision)

The obvious cheap fix, generalize the floor and move the anchor `opener.Component<Linker>()` statement
below `var door`, is unsafe and is rejected here.

`StatementPlacement` RULE 1 (`:16-23`) makes a statement's block position its SCENE-GRAPH sibling
index: `BuilderParser` derives a component's ordinal-within-type from the ORDER of the `.Component<T>()`
statements feeding the same receiver (`AssignComponentLogicalIds`, `BuilderParser.cs:762`, composing
`ComponentTargetResolution.ComposeLogicalId(node.LogicalId, TypeFullName, ordinal)` at `:769`). If
`opener` has more than one component and the forward-referring one is not last, sliding it below
`var door` slides it past its component peers, so the emission re-parses to a different component
ordinal, and the next sync re-Reorders it. Satisfying RULE 2 by relocation would violate RULE 1 for
any node with multiple components. The deferred separate statement avoids the collision: it does not
feed the receiver's component list (it configures an already-authored component), so relocating it
changes no sibling ordering, exactly the property RULE 2's "move the dependent group" relies on
(`:26-32`).

The measured repro has a single component, where naive relocation happens to be order-safe. Fixing only
that case would leave the multi-component case broken and is not spec-compliant.

## The decided design: a capturable component handle (Shape B)

The reference-introducing field-set needs a way to be its OWN statement, configuring an ALREADY-authored
component without re-adding it and without reordering it, round-tripping through `BuilderParser`. A
type-keyed deferred setter and an ordinal-baked setter both fail on duplicate-component identity (see
point 3). The decided shape is a captured component handle:

```
var opener = scene.Add("Opener");
var linker = opener.Component<Linker>();
var door = scene.Add("Door");
linker.Set("target", door);          // deferred config of THIS component, seated after door
```

The anchor's `.Component<T>()` statement stays where it is (no component reorder). The deferred
`linker.Set(...)` is a new statement seated by the generalized floor, after `door`'s declaration.

1. **`Component<T>()` returns a capturable `ComponentHandle<T>`.** Today `NodeHandle.Component<T>()`
   (`com.codescenes/Runtime/NodeHandle.cs:101`) and its closure overload (`:108`) return `NodeHandle`,
   and the config closure is the only way to set fields. Both change to return `ComponentHandle<T>`
   (`com.codescenes/Runtime/ComponentHandle.cs:14`, a `sealed partial` class), so the author can
   capture the call into a `var`. The closure stays OPTIONAL: a call whose return value is ignored,
   `opener.Component<Linker>(c => c.Set("speed", 5))`, and the bare `opener.Component<Linker>();`,
   compile and behave exactly as before. This is an ADD to the public surface.

   **Backward-compatibility is a hard, tested requirement, not an assumption.** `.Component<T>(...)`
   returns `NodeHandle` today precisely so further calls chain off its result. The corpus chains a
   trailing `.Ref<T>()` after it (`NodeHandle.Ref<T>` at `NodeHandle.cs:113`), e.g.
   `var door = scene.Add("Door").Component<DoorOpener>(_ => { }).Ref<DoorOpener>();`
   (`SceneBuilder.Core.Tests/UnityEventReconcileSourcePatchTests.cs:404`,
   `SceneBuilder.Core.Tests/UnityEventAuthoringFormsTests.cs:32,59,82`), and captures the whole chain
   into a `var` (`SceneBuilder.Core.Tests/DefaultResetRemovalTests.cs:64`). Changing the return type
   MUST NOT break any chained form that compiles today. A full corpus scan finds EXACTLY TWO
   continuations chained off `.Component<T>(...)`: `.Ref<U>(int ordinal = 0)` (17 sites, e.g.
   `.Component<DoorOpener>(_ => { }).Ref<DoorOpener>()`) and a further `.Component<U>()` /
   `.Component<U>(closure)` (9 sites, e.g.
   `crate.Component<UnityEngine.BoxCollider>().Component<UnityEngine.Rigidbody>()` and the duplicate
   form `.Component<DoorOpener>(_ => { }).Component<DoorOpener>(_ => { }).Ref<DoorOpener>(1)` at
   `SceneBuilder.Core.Tests/UnityEventAuthoringParseTests.cs:72`).

   **RULING (decided, not left to the blueprint): change the return type AND give `ComponentHandle<T>`
   those same continuations** — `Ref<U>(int ordinal = 0)` returning `ComponentRef<U>`, and
   `Component<U>()` / `Component<U>(Action<ComponentHandle<U>>)` returning `ComponentHandle<U>`. Because
   every Runtime handle method is a non-executed marker stub (`NodeHandle` and `ComponentHandle<T>`
   members are all `=> this` / `=> new`, parsed from source text, never run), this is a
   source-compatible surface addition: every chain that compiles today still compiles, the parser reads
   the identical source text, and no chained form changes shape. The blueprint owns only the mechanics
   (the exact stub members), not the approach. A regression test over the shipped corpus proving it
   still compiles and round-trips unchanged is a REQUIRED deliverable of the Runtime task.

2. **The captured handle's DEFERRED `.Set(field, value)`.** `ComponentHandle<T>.Set(string, object)`
   already exists (`ComponentHandle.cs:17`, returning `ComponentHandle<T>`); no new runtime method is
   needed. What is new is the EMISSION and PARSE of it as a standalone statement on a captured handle
   (`linker.Set("target", door);`), rather than only inside the `Component<T>(c => c.Set(...))`
   closure. It uses the string-path spelling, matching the append path's fixed spelling (spec 35 D4).

3. **Duplicate-component safety is the reason for the handle.** Component identity is
   `Type.FullName` + ordinal-within-type: two `Linker`s on `opener` are `Linker#0`/`Linker#1`
   (`ComponentTargetResolution.ComposeLogicalId`, applied at `BuilderParser.cs:769`). A type-keyed
   deferred setter (`opener.Set<Linker>(...)`) CANNOT disambiguate them, and an explicit ordinal
   argument bakes an index into source that goes stale the moment a component is deleted or reordered.
   The captured `var` names the specific component INSTANCE, bound at its own `.Component<T>()`
   statement exactly as `var door = scene.Add("Door")` binds a GameObject, so it survives reordering.
   A two-same-type-component forward reference is in the RED matrix and the Unity checklist below.

4. **`BuilderParser` round-trip.** The parser must recognize `var h = <recv>.Component<T>()` and bind
   `h` to that component (Type#ordinal by declaration order among same-type components on the
   receiver), and recognize `h.Set(...)` as deferred field config on that bound component. This extends
   two existing bindings:
   - The variable-to-handle binding for GameObjects: `var door = scene.Add(...)` runs through
     `ProcessStatement`'s local-declaration arm (`BuilderParser.cs:244`) into `ProcessAddChain`
     (`:337`), which binds the name in `BindChainHandle` (`BuilderParser.UnityEvents.cs:75`) via
     `ctx.Handles[handleName] = node` (`:89`).
   - The component-handle binding already used for `.Ref<T>()`: `BindChainHandle` routes a trailing
     `.Ref<T>()` into `ctx.ComponentRefs[handleName]` (`BuilderParser.UnityEvents.cs:85`), resolved
     against the final component ordinals in `ResolveComponentRefs` (`:98`, composing the same
     `ComponentTargetResolution.ComposeLogicalId` at `:103`) and surfaced as the `ComponentHandles`
     map on `ParseResult`. A captured `var h = recv.Component<T>()` binds the same component-handle
     space, but points at the component `ApplyComponent` (`BuilderParser.cs:487`) just authored rather
     than an already-declared one. The lone `recv.Component<T>()` statement is already recognized and
     marked `StatementAnchored` (`BuilderParser.cs:329`); this adds its captured-var and later
     `h.Set(...)` forms. `h.Set(...)` is a new statement-level receiver dispatch in `ProcessBuilderChain`
     (`:273`, receiver-lookup mirror of `:346`) that appends a field to the bound component's `Fields`,
     reusing `ProcessComponentSetCall`'s `Set` arm (`:560`). Round-trip: emit then parse must recover
     the same component and field.

5. **Emission policy.** The reconciler emits the deferred capture-plus-`.Set` form ONLY when the
   in-closure fold would forward-reference (the value names a handle declared later than the anchor).
   Otherwise it keeps the existing in-closure fold at
   `ComponentPatchApplier.ResolveIntroduceComponentField` (`:187`), so existing generated files do not
   churn. A task must not "helpfully" rewrite every field-set to the deferred form.

## In scope

- Generalize `StatementPlacement`'s floor to a max-declaration-index over every handle a placed
  statement names, receiver and arguments, at the single placement path; expose, on the
  reference-introducing edit, the set of handle identifiers its rendered value uses (or parse them at
  placement time) so the floor can see argument handles.
- Change `NodeHandle.Component<T>()` and its closure overload to return `ComponentHandle<T>`,
  backward-compatibly, so a component handle can be captured, with a regression test proving the
  shipped corpus still compiles and round-trips.
- Recognize `var h = recv.Component<T>()` and its deferred `h.Set(...)` in `BuilderParser`, binding the
  handle to the specific component by Type#ordinal, and round-tripping.
- Emit the reference-introducing field-set as a deferred, placement-routed `h.Set(...)` statement on a
  captured handle whenever its value names a later-declared handle; keep the in-place closure fold for
  the ordinary case where every named handle is already declared earlier.
- Document the new public surface (`ComponentHandle<T>` return of `Component<T>()` and its deferred
  `.Set`) in XML doc comments and `api.json`.
- The structural forward-reference check plus a Roslyn compile assertion, driven through the live
  two-sync sequence, not one trusted site.

## Out of scope

- The `Pending`-then-converge behavior of sync 1. Omitting the still-unmapped target on the first sync
  is correct (spec 41 report-channel contract); the bug is sync 2's placement, not sync 1's deferral.
- A deferred setter for prefab-instance components (`InstanceHandle.AddComponent<T>` /
  `ScopedHandle.AddComponent<T>`). Those return the instance/scope handle and use `.Override(...)` for
  post-hoc config; the repro is a plain-GameObject `NodeHandle.Component<T>`. Instance/scope forward
  references, if reproduced, are a separate item.
- The other open defects sharing `FOUND-BY: reference-writes-and-cache-invalidation` (double-emit,
  cold-assemble perf, uncached ref resolver, per-keystroke skipped-field logging). Unrelated subsystems.
- Performance of the placement pass. The floor computation is O(statements × named handles) per placed
  statement, no new whole-scene walk.

## Additions to the contract

The generalized floor is a change to an existing private helper on `StatementPlacement`, no new Core
value type and no new `ConflictKind`. The deferred-config shape is one new `SourceEdit` emission
shape (or an existing introduce edit gaining a "seat as its own captured statement" mode) plus its
applier; the blueprint decides whether it is a new record or a mode flag on `IntroduceComponentField`.
Whichever it is, its placement goes through `PlaceNewStatement` / `PlacementIndex`, never an in-place
rewrite, so it inherits the floor. The one public-surface change is the return type of
`NodeHandle.Component<T>()`, an additive change bound by the backward-compatibility requirement above.

## Core deliverables

- `StatementPlacement.MinIndexAfterReceiverDeclaration` (`SceneBuilder.Core/Reconcile/StatementPlacement.cs:133`)
  generalizes to a floor over ALL handles a statement names, consumed by `PlacementIndex` at `:244`.
  Every placement-routed edit inherits it. A helper enumerates the in-block local handles a statement
  references (receiver via `RootReceiverName` at `:86`, arguments via the rendered expression's
  identifiers), reusing `DeclarationIndexOf` (`:113`).
- The reference-introducing field-set, when its value forward-references, is emitted as a deferred
  captured `h.Set(...)` statement seated by that floor after the target's declaration, at
  `SceneBuilder.Core/Reconcile/ComponentReconciler.cs:478`/`:529` (emit) and its applier (place via
  `PlaceNewStatement`, do not fold in place). The in-place fold at `ComponentPatchApplier.cs:187`
  (`:233-269`) stays the path for the no-forward-reference case. When the anchor component has no
  captured var yet, the applier binds one on the existing anchor statement in place
  (`opener.Component<Linker>();` becomes `var linker = opener.Component<Linker>();`) so the deferred
  `.Set` can name it; the anchor statement does not move.
- `BuilderParser` recognizes `var h = recv.Component<T>()` (bind `h` to the authored component by
  Type#ordinal, extending `BindChainHandle` / the `ctx.ComponentRefs` + `ResolveComponentRefs` path in
  `BuilderParser.UnityEvents.cs`) and `h.Set(...)` (statement-level deferred field config, a new
  receiver arm in `ProcessBuilderChain` reusing `ProcessComponentSetCall`'s `Set` handling). Emit then
  parse recovers the same component and field.
- The structural forward-reference check (generalizing
  `SceneBuilder.Core.Tests/SelfReferenceEmitTests.cs:48`): no statement names an in-block local
  declared at or after its own position, asserted over the applied source of the two-pass simulation.
- A headless two-pass simulation of the live repro (`Opener` with `Linker.target` unauthored, `Door`
  appended as a later root, target wired on the second pass) whose applied source is compiled by
  `AuthoringBindHarness.BindErrors` and asserted to produce zero errors (specifically no `CS0841`), and
  whose third pass over the converged source produces zero edits (fixed point).

## Runtime and authoring deliverables

- `NodeHandle.Component<T>()` (`com.codescenes/Runtime/NodeHandle.cs:101`) and
  `NodeHandle.Component<T>(Action<ComponentHandle<T>> configure)` (`:108`) return `ComponentHandle<T>`.
  Every chained form that compiles today still compiles: the `.Ref<U>()` continuation
  (`NodeHandle.cs:113`) exercised at `UnityEventReconcileSourcePatchTests.cs:404` and
  `UnityEventAuthoringFormsTests.cs:32,59,82`, and the captured chained form at
  `DefaultResetRemovalTests.cs:64`. The blueprint chooses the mechanism that preserves them; a
  regression test over the shipped corpus proving unchanged compile-and-round-trip is required.
- `ComponentHandle<T>.Set(string serializedPath, object value)` (`ComponentHandle.cs:17`) is reused as
  the deferred setter; its XML doc comment states it may stand as its own statement on a captured
  handle, not only inside the `Component<T>(...)` closure. No new runtime `Set` method.

## DocGen deliverables

`ComponentHandle<T>` as the return of `Component<T>()`, and its deferred `.Set`, are new PUBLIC API.
`SceneBuilder.DocGen` reads XML doc comments syntactically over `com.codescenes/Runtime`
(`SceneBuilder.DocGen/Program.cs:19`, `TypeExtractor.FromDirectory`) into `api.json`, the published
authoring reference the marketing site renders.

- The changed `NodeHandle.Component<T>` overloads and the `ComponentHandle<T>` capture-plus-deferred-`.Set`
  usage carry accurate XML doc comments on `com.codescenes/Runtime` (published API copy read by users).
- A check that the new surface is present in a freshly regenerated `api.json` (run
  `dotnet run --project SceneBuilder.DocGen -- --out <path>/api.json` and assert `ComponentHandle<T>`
  and its `Set` appear with docs).

## Editor adapter deliverables

The read path that produces the forward-reference is Unity-observable: the target is a scene ROOT
created after the referrer, and the reference is wired in a live scene across two manual syncs. That
sequence is not reachable from a POCO fixture, so it needs an EditMode test in
`unity-gate/Assets/GateTests/` that reproduces it against a real editor scene and asserts the emitted
source compiles. Per the CLAUDE.md hard requirement, a Unity-observable behavior is not complete
without EditMode coverage that exercises the real boundary; the headless Core simulation proves the
placement and parse halves but is structurally blind to the real `SceneBuilderSync.Run` compile check
(`EmittedCodeCompiles.SyncAndAssertCompiles`).

## Decomposition

Dependency-ordered. Every task keeps a complete `TOUCHES` list; a task that omits a file it edits
mis-scopes the gate. Tasks that touch `com.codescenes/Runtime` or `BuilderParser` take the FULL Unity
gate (noted per task); Core-only tasks fast-gate.

- **Task A — the generalized floor and its check [Core, fast-gate].** Generalize the
  `StatementPlacement` floor to the max-declaration-index over all named handles, add the
  handle-enumeration helper, and add the structural forward-reference check. This is the one placement
  mechanism every placing edit inherits; it owns the check. Depends on nothing. TOUCHES:
  `SceneBuilder.Core/Reconcile/StatementPlacement.cs`, `SceneBuilder.Core.Tests/**`.
- **Task B — capturable `ComponentHandle<T>` return plus docs [Runtime/authoring, FULL gate].** Change
  `NodeHandle.Component<T>()` and its closure overload to return `ComponentHandle<T>`; preserve every
  chained corpus form; update XML docs; regenerate and check `api.json`. Depends on nothing (pure
  surface). TOUCHES: `com.codescenes/Runtime/NodeHandle.cs`, `com.codescenes/Runtime/ComponentHandle.cs`,
  `SceneBuilder.DocGen/**` (if a check helper is added), `unity-gate/Assets/GateTests/**` (an EditMode
  corpus-compat round-trip), `SceneBuilder.Core.Tests/**` (headless corpus compile regression). Takes
  the full gate because it changes `com.codescenes/Runtime`.
- **Task C — `BuilderParser` recognition and round-trip [Core, FULL gate].** Recognize
  `var h = recv.Component<T>()` (bind by Type#ordinal) and `h.Set(...)` (deferred field config), and
  round-trip both. Depends on B (the authoring shape it parses must compile). TOUCHES:
  `SceneBuilder.Core/Parsing/BuilderParser.cs`, `SceneBuilder.Core/Parsing/BuilderParser.UnityEvents.cs`,
  `SceneBuilder.Core/Parsing/BuilderParser.Recognition.cs` (if the statement shape is recognizer-gated),
  `SceneBuilder.Core.Tests/**`. Takes the full gate because it changes the parser.
- **Task D — reconciler emits the deferred form for forward refs [Core, fast-gate].** Emit the
  reference-introducing field-set as a deferred, placement-routed captured `h.Set(...)` when its value
  forward-references; bind a capture var on the anchor when it has none; keep the in-place fold
  otherwise. Seated by Task A's floor; parsed by Task C. Depends on A, B and C. TOUCHES:
  `SceneBuilder.Core/Reconcile/ComponentReconciler.cs`,
  `SceneBuilder.Core/Reconcile/ComponentPatchApplier.cs`,
  `SceneBuilder.Core/Reconcile/SourceEdit.cs` (if a new record or mode flag is added),
  `SceneBuilder.Core.Tests/**` (the two-pass compile simulation).
- **Task E — live EditMode two-sync gate test [adapter, FULL gate].** In a real scene, drive the
  two-sync repro with a scene-ROOT target, and a two-same-type-component variant, asserting the emitted
  source compiles. Depends on D. TOUCHES: `unity-gate/Assets/GateTests/**`.

## Core and adapter test plan

RED tests, behavior not structure:

- **Task A (Core).** Over an applied source where a statement forward-references a later-declared local,
  the generalized floor seats it after the declaration and the structural check fails on the un-fixed
  tree, passes on the fixed one. Drive it through applied output, not a hand-written string.
- **Task B (Core headless + EditMode).** The shipped corpus (round-trip fixtures and the
  `.Component<T>(...).Ref<T>()` / captured-chain forms) still compiles and round-trips after the return
  type change; an EditMode round-trip proves a real editor scene still emits and re-parses those forms.
- **Task C (Core).** `var h = recv.Component<T>(); h.Set("target", door);` parses to the same component
  (correct Type#ordinal) and field the folded form produces; a hand-written two-same-type case binds
  `Linker#0` and `Linker#1` to distinct handles.
- **Task D (Core).** Simulate the live sequence: pass 1 appends `Door` as a later root and defers the
  pending target; pass 2 wires `Opener.Linker.target = Door`. Assert the applied source, compiled
  through `AuthoringBindHarness.BindErrors`, yields zero errors and no `CS0841`, the deferred `h.Set`
  is seated after `var door`, the anchor `.Component<Linker>()` did not move, and a third pass is a
  fixed point (zero edits). A no-forward-reference control keeps the in-place closure fold (no churn).
- **Task E (EditMode, required).** In a real scene, converge an `Opener` with an unauthored
  `Linker.target`, create a new ROOT `Door` at end-of-roots, wire `Opener.Linker.target = Door`, and
  run two manual syncs through `EmittedCodeCompiles.SyncAndAssertCompiles`. Assert the second sync's
  builder source compiles (`BuilderCompileCheck` stays green, no `CS0841`) and the reference
  round-trips, and a third sync is a fixed point. Repeat with `Opener` carrying two `Linker`s, the
  forward-referring one authored first.

## Unity confirmation checklist

These become the EditMode tests above.

1. Converge a scene with one root `Opener` carrying a `Linker` component whose reference field is
   unauthored. Add a new root object `Door` to the scene, positioned after `Opener`. Set
   `Opener`'s `Linker.target` to `Door`. Sync once, then sync again. Expected: the builder source
   compiles after both syncs; the field is authored as a deferred `linker.Set("target", door)` seated
   after `var door`, with the `.Component<Linker>()` anchor unmoved; `Opener.Linker.target` resolves to
   `Door`; a third sync produces zero edits.
2. Repeat item 1 with `Opener` carrying two `Linker` components, the forward-referring one authored
   first. Sync twice. Expected: the source compiles; the deferred set names the correct component
   INSTANCE (`Linker#0`, not `Linker#1`); the two components keep their authored order (no component
   reorder churn on the following sync); a third sync is a fixed point.
3. Repeat item 1 with a second component of a DIFFERENT type also on `Opener`, the forward-referring
   `Linker` authored first. Sync twice. Expected: the source compiles, the components keep their
   authored order, and a third sync is a fixed point (the deferred statement, not being a
   `.Component<T>()` call, does not perturb component ordinals).

## Dependencies

- M2 reconcile (`SourceEdit`, `SourcePatchApplier`, `StatementPlacement`).
- M4/M5 references (`ObjectRef` and its handle rendering, `RenderFieldValue`).
- Spec 41 (`specs/completed/41-snapshot-emit-classification.md`): the `Pending`-defer contract that
  produces sync 1's silent omission, and the classification this spec's emit sites already route
  through. This spec is the root-append variant 41 explicitly deferred (`41:72-73`).
- Spec 35 D4 (`specs/completed/35-reference-writes-and-cache-invalidation.md`): the append path's fixed
  string spelling the deferred `.Set` reuses.
- `SelfReferenceEmitTests`: the self-reference degenerate case this spec generalizes.

## Risks and notes

- Return-type change is the sharpest risk. `.Component<T>(...)` returns `NodeHandle` today so calls
  chain off it; a naive change breaks the `.Ref<T>()` continuation and every captured chain. Task B's
  corpus regression is the gate on this, and it is not optional.
- The generalized floor must enumerate argument handles from the RENDERED value expression, whose
  identifiers are the target locals. A naive identifier scan could over-count (a type name or a member
  access, not a local). Scope the scan to identifiers that match an in-block local declaration, which
  `DeclarationIndexOf` already resolves; a non-local identifier resolves to -1 and contributes nothing.
- The deferred shape must round-trip: `BuilderParser` has to read `var h = recv.Component<T>()` plus
  `h.Set(...)` back into the same model the folded form produces, or the next sync sees a phantom diff.
  The two-pass fixed-point assertion (third pass, zero edits) is what proves this; it is not optional.
- Keep the in-place fold for the common no-forward-reference case. Switching every reference field to a
  deferred statement would churn source that compiles today. The deferred shape is conditional on the
  value naming a later-declared handle.
</content>
</invoke>
