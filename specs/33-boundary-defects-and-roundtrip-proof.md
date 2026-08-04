# Spec 33 — Boundary defects and round-trip proof

## Build order

C9, then C10 — both are active defects on `main` and C9 is data loss on the happy path. Then C5, C6,
C7, C8, then the round-trip proof suite.

This is EXECUTION order, not a dependency claim: no item here needs another item's code. Encode it
as serial ordering, and where an ordering-only edge is used, say so on the edge. Do not invent a
data dependency to express a preference, and do not encode the order for some pairs and drop it for
others.

Commit `795eba2` already carries UNVALIDATED partial work for C5 and C6:
`com.codescenes/Editor/ComponentTypeResolver.cs` (short-name resolution across loaded assemblies,
which reached a GREEN task verdict) and `SerializedFieldExclusions.cs` (the inspector-visibility
rule, whose behavioral claim was escalated as unproven). Neither passed a seam review. Re-validate
and finish that code; do not rebuild it from scratch, and do not assume it works.

## C9 — clearing a scene reference from code is silently discarded

Authoring `c.Set("m_TargetGraphic", NodeHandle.None)` against a scene where that reference is
assigned does not clear it. The build reports `0 plan op(s)`, the live value is unchanged, and the
next scene→code sync rewrites the source line back to the old reference. The author's edit is lost
with no conflict, no warning and no error.

Reproduces on both reference kinds, which disagree on op count and so need isolating between plan
generation and plan execution:
- native component PPtr (`Button.m_TargetGraphic`) — `0 plan op(s)`
- plain `public GameObject` field on a user MonoBehaviour — `1 plan op(s)`, both with the string
  path and the typed `c.Set(x => x.target, NodeHandle.None)` spelling

Implicated: `SceneBuilder.Core/Reconcile/ComponentReconciler.cs` (emits `"NodeHandle.None"` around
line 504) and the editor-side `ObjectRef(null)` write path.

**Build:** a code-authored `NodeHandle.None` clears the live reference, and the source is a fixed
point afterwards.

**Accept when:** both reference kinds clear on build, a second sync produces zero edits, and the
authored `NodeHandle.None` is still in the source. This is data loss on the happy path — it takes
priority over the rest of this spec.

## C10 — a self-referencing component emits non-compiling source

A component whose reference field points at another component on the SAME GameObject emits a
statement that uses the node's local variable inside its own declaration:

```csharp
var button = canvas.Add("Button")
    .RectTransform(anchoredPos: (0f, -60f), sizeDelta: (160f, 40f))
    .Component<UnityEngine.UI.Image>()
    .Component<UnityEngine.UI.Button>(c => c.Set("m_TargetGraphic", button));
```

`CS0841: Cannot use local variable 'button' before it is declared.`

This is the default shape of every Unity Button — `targetGraphic` is set to the Image on its own
GameObject at creation — so any scene containing a button emits a builder file that does not compile.
Sync continues to work (the grammar parses, C# is never compiled by the sync path), so the damage is
the IDE, IntelliSense and `SceneBuilder.Validate`.

Scoped by experiment: the defect is specific to the SELF-reference. Retargeting the same field to a
`Graphic` on a DIFFERENT object emits correctly (`c.Set("m_TargetGraphic", panel)`) and passes the
compile check; pointing it back at the object's own Image reproduces CS0841 immediately.

It does not self-heal. The broken line is carried forward on every later sync, so a subsequent
unrelated edit still re-reports DOES NOT COMPILE and the file stays broken until a human fixes it.

The emitter already splits a follow-up call onto its own statement when it must
(`button.Component<UnityEngine.CanvasRenderer>();` on the next line), so the mechanism to fix this
exists; a self-reference is not routed through it.

**Build:** a reference to a component on the node currently being declared emits in a form that
compiles — a follow-up statement after the declaration, or a self-selector that needs no variable.

**Accept when:** a Canvas/Image/Button/Text scene round-trips to source that compiles, and a second
sync is a fixed point. Cover the same-GameObject case explicitly; a cross-object reference already
works and is not the defect.

**Fix forward only.** A builder file that ALREADY carries a broken self-reference line is out of
scope: nothing detects and rewrites it. No such file exists today (no builder in the test project
contains the pattern) and nothing has shipped, so the fix leaves no one holding a broken file.

## C5 — fail-loud on an ambiguous short name

Short-name resolution across loaded assemblies works, and the unambiguity check works: an ambiguous
short name resolves to null and returns the candidate list rather than guessing.

Nothing reports those candidates. `ResolveObjectTypeByShortName` hands them back through an
out-parameter no caller surfaces, so "fail loud and located" is half-honoured — it fails, silently.

**Build:** report the ambiguity where the author can see it, naming the colliding types.

**Accept when:** authoring against a type whose short name collides produces a located message
naming both candidates, rather than a silent null.

**Split across the two suites**, because resolution needs Unity's loaded assemblies but the report
does not:
- **Core** — the candidate list and its message form are Unity-free and unit-tested headlessly: two
  colliding candidates in, one message naming both out. This is the spec's Core test-plan line; it
  is met, not waived.
- **EditMode** — a real short name colliding across loaded assemblies reaches that message through
  `com.codescenes/Editor/ComponentTypeResolver.cs`, whose only non-test caller is
  `ObjectReferenceResolver.cs`.

The message is built by the located report channel below, not by a second formatter.

## C6 — the sorting-layer field pair

A field a user cannot set is not author intent and must not reach builder source. Enforcement is a
curated exclusion list in `SerializedFieldExclusions.NotInspectorAuthorable`; every entry must be
justified by that rule, not by taste.

`SpriteRenderer.m_WasSpriteAssigned` and `m_Size` are excluded and hold. Open: `SpriteRenderer`
serializes both `m_SortingLayer` and `m_SortingLayerID` where the Inspector shows one dropdown
(Canvas serializes only the ID). Both currently round-trip.

**Build:** determine whether the pair is one control writing two fields and, if so, exclude the
redundant one. Proving it needs a second sorting layer in `ProjectSettings/TagManager.asset`.

**Accept when:** one Inspector control yields at most one authored field.

## C7 — visibility of an unrepresentable field

A `UnityEvent` field cannot be represented in builder source: its serialized members
(`m_PersistentCalls`) have no compiling initializer form. Every UI component carrying one emits a
console NOTE and nothing else, so wiring a `Button.onClick` in the Inspector produces no code and the
scene edit is not synced. A console warning is easy to miss.

**Build:** console-only, promoted from a NOTE to a located warning that names the object (its
`LogicalId`, plus the live scene hierarchy path where the adapter has it), the component type and
the field, and states that the value stays in the scene and is not synced.

**Sync NEVER writes an advisory comment into builder source.** The builder file holds what the author
wrote and what their scene edits patched into it, nothing else. A marker for an unrepresentable field
is not author intent, and `specs/09` makes the motivating case (`UnityEvent` persistent calls)
representable for real, so a marker would be dead within a milestone.

The path is general, not UnityEvent-specific: it fires for any field the value model cannot
represent. After `specs/09` lands it stops firing for `onClick` and keeps covering the rest.

Representing persistent calls is `specs/09`, not this spec.

**Accept when:** wiring an `onClick` in the Inspector produces a console warning that names the
object, the component type and the field AND states that the value stays in the scene and is not
synced; the builder source is byte-unchanged by the warning path. All four parts are asserted — a
test that checks only the located triple leaves the author without the one sentence that tells them
what happened to their edit.

The warning goes through the located report channel below. It is not a second surfacing path.

## C8 — a no-op source edit issues a write op

A pure member reorder of a struct initializer, changing no value, produces
`Built in place: 1 plan op(s)` rather than zero. The following sync reports `Scene already matches
code`, so it converges and corrupts nothing.

**Build:** measure whether that op sets the scene dirty flag, in an EditMode test that records the
answer either way. The measurement is the deliverable; what follows it depends on the result.

Both branches carry their own assertion, so neither closes on prose:
- **It dirties the scene** — suppress the op. The test asserts a value-preserving reorder yields
  **zero** plan ops and leaves the dirty flag unset. Otherwise every keystroke inside a struct
  initializer marks the scene dirty.
- **It does not dirty the scene** — build nothing further. The test stands as the recorded
  measurement: the reorder yields its op and the dirty flag stays unset.

## The round-trip proof suite

**Build:** bidirectional EditMode coverage — for each behavior, drive the change from both
directions against real components and assert convergence (a second sync produces zero edits).

**Required scope:**
1. `Camera`, `MeshRenderer`, `SpriteRenderer`, `Light`.
2. A struct nested inside a struct, at least two levels deep.
3. Prefab-instance context, which reads through `PrefabInstanceProbe` rather than the plain reader.
4. Lists of structs. `ExcludeUnrepresentable` does not filter list items, carried as an explicit
   `InList` flag; pin that behavior.

**The shared harness lives in its own file**, `unity-gate/Assets/GateTests/RoundTripProofHarness.cs`,
and every test file that drives it declares that path. A harness that lives inside the first test
file it was written for means every later test edits a file it never declared.

**Accept when:** the suite is green in the batchmode gate AND a live-editor pass confirms it. A
batchmode gate alone does not close this spec. The live pass writes its console log to
`unity-gate/Logs/live-verify-roundtrip.log` and its observation-to-evidence report to
`.agent_handoffs/<feature>/live-verify-roundtrip.md`, each observation citing a line in that log. An
evidence artifact with no path is a narrated pass.

## The cross-cutting invariant: one located report channel

C5 and C7 both end in the same act — telling an author something about their scene did not reach
their code. That report is this spec's invariant, it gets ONE owning task and ONE implementation
every other site calls, and the enforcement must be structural.

**Every report of this kind carries, as REQUIRED construction data:** the object anchor (the
`LogicalId`, plus the live scene hierarchy path wherever the adapter has it), the component type
full name, and the field or member key. Required means non-optional parameters, so a site that omits
them does not compile.

**Where it binds.** All three `ConflictKind.UnrepresentableValue` factories in
`SceneBuilder.Core/Reconcile/ConflictDetector.cs` take that data today only in part —
`UnrepresentableNestedFieldMembers` carries `componentTypeFullName`, `UnrepresentableNestedMember`
and `UnrepresentableNestedField` do not. All three must, and a fourth added later must not be able
to skip it.

**The bypass to close.** `ConflictSurfacing.LogNote(string logicalId, string message)` is public and
takes no location, and `SurfaceNotes` routes every standing note through it. A site that reaches the
console that way emits an unlocated report and no adapter-side check can see it, because the message
text is built in Core and arrives as runtime data. An `UnrepresentableValue` note must not be
loggable through an untyped two-argument call.

**Enforcement lives in Core, not in an adapter source scan.** The check is a Core test asserting
that every `UnrepresentableValue` conflict the detector produces carries the required data. A grep
over `com.codescenes/**/*.cs` for message-copy constants cannot work: the copy lives in Core, so the
scan's scope excludes the layer that owns the strings, and a bypassing site is invisible to it.

`SceneBuilder.Core/Model/ValueWalk.cs` is the model to follow — a pass declares what it does to a
node and how it descends, so failing to recurse is not expressible. Apply the same standard here:
make the omission fail to compile, rather than checking for it after the fact.

**Message copy ownership.** The report text is built in
`SceneBuilder.Core/Reconcile/ConflictDetector.cs` and asserted by
`SceneBuilder.Core.Tests/UnrepresentableNoteTests.cs` and
`SceneBuilder.Core.Tests/NestedStructEmitCompileTests.cs`. Whichever task changes the copy declares
all three, plus `SceneBuilder.Core/Reconcile/NestedValueEmission.cs` if the emission path moves.

## Test plan

- **Core:** a `NodeHandle.None` on an assigned reference lowers to a clearing op, for both a native
  PPtr and a MonoBehaviour `GameObject` field (C9); two colliding candidates produce one message
  naming both (C5); every `UnrepresentableValue` conflict the detector produces carries the required
  located data (the invariant).
- **EditMode** (required — adapter changes): a code-authored `NodeHandle.None` clears the live
  reference and the source is a fixed point afterwards (C9); a real colliding short name reaches the
  C5 message; a wired `onClick` produces the C7 warning and leaves the source byte-unchanged; the
  C8 dirty-flag measurement; the round-trip suite above.
- **Live:** a real-editor pass over C9 in both reference kinds, and over the C7 console warning.
