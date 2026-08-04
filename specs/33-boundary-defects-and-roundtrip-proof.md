# Spec 33 — Boundary defects and round-trip proof

## Build order

C9, then C10 — both are active defects on `main` and C9 is data loss on the happy path. Then C5, C6,
C7, C8, then the round-trip proof suite. Nothing here depends on a later item.

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

## C5 — fail-loud on an ambiguous short name

Short-name resolution across loaded assemblies works, and the unambiguity check works: an ambiguous
short name resolves to null and returns the candidate list rather than guessing.

Nothing reports those candidates. `ResolveObjectTypeByShortName` hands them back through an
out-parameter no caller surfaces, so "fail loud and located" is half-honoured — it fails, silently.

**Build:** report the ambiguity where the author can see it, naming the colliding types.

**Accept when:** authoring against a type whose short name collides produces a located message
naming both candidates, rather than a silent null.

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

**Build:** console-only, promoted from a NOTE to a located warning that names the scene object path,
the component type and the field, and states that the value stays in the scene and is not synced.

**Sync NEVER writes an advisory comment into builder source.** The builder file holds what the author
wrote and what their scene edits patched into it, nothing else. A marker for an unrepresentable field
is not author intent, and `specs/09` makes the motivating case (`UnityEvent` persistent calls)
representable for real, so a marker would be dead within a milestone.

The path is general, not UnityEvent-specific: it fires for any field the value model cannot
represent. After `specs/09` lands it stops firing for `onClick` and keeps covering the rest.

Representing persistent calls is `specs/09`, not this spec.

**Accept when:** wiring an `onClick` in the Inspector produces a console warning naming that object,
component and field; the builder source is byte-unchanged by the warning path.

## C8 — a no-op source edit issues a write op

A pure member reorder of a struct initializer, changing no value, produces
`Built in place: 1 plan op(s)` rather than zero. The following sync reports `Scene already matches
code`, so it converges and corrupts nothing.

**Build:** measure whether that op sets the scene dirty flag. If it does, suppress the op for an edit
that changes no value — otherwise every keystroke inside a struct initializer marks the scene dirty.
If it does not, close this with the measurement recorded and build nothing.

## The round-trip proof suite

**Build:** bidirectional EditMode coverage — for each behavior, drive the change from both
directions against real components and assert convergence (a second sync produces zero edits).

**Required scope:**
1. `Camera`, `MeshRenderer`, `SpriteRenderer`, `Light`.
2. A struct nested inside a struct, at least two levels deep.
3. Prefab-instance context, which reads through `PrefabInstanceProbe` rather than the plain reader.
4. Lists of structs. `ExcludeUnrepresentable` does not filter list items, carried as an explicit
   `InList` flag; pin that behavior.

**Accept when:** the suite is green in the batchmode gate AND a live-editor pass confirms it. A
batchmode gate alone does not close this spec.

## Decomposition constraint

Name this spec's cross-cutting invariant precisely and give it ONE owning task with ONE shared
implementation that every other task calls. Do not restate an invariant as per-task guidance.

For C5 the candidate is type resolution itself: one resolver every call site uses, rather than each
site parsing and matching its own way.

`SceneBuilder.Core/Model/ValueWalk.cs` is the model to follow — a pass declares what it does to a
node and how it descends, so failing to recurse is not expressible.

## Test plan

- **Core:** a `NodeHandle.None` on an assigned reference lowers to a clearing op, for both a native
  PPtr and a MonoBehaviour `GameObject` field (C9); an ambiguous short name surfaces its candidates
  rather than returning a bare null (C5).
- **EditMode** (required — adapter changes): a code-authored `NodeHandle.None` clears the live
  reference and the source is a fixed point afterwards (C9); the round-trip suite above.
- **Live:** a real-editor pass over C9 in both reference kinds, and over whichever surface C7 lands
  on. C8 is a measurement before it is a build item.
