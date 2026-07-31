# Spec 33 — Boundary defects and round-trip proof

## C5 — PPtr type resolution by short name

`ObjectReferenceResolver.ExpectedRefType` parses a `SerializedProperty.type` of the form `PPtr<$X>`
to the bare token `X` and hands it to `ComponentTypeResolver.Resolve`, which matches on
`Type.FullName`. `Sprite` does not equal `UnityEngine.Sprite`; `Graphic` does not equal
`UnityEngine.UI.Graphic`. Only `GameObject` is special-cased. Namespaced types never resolve, and
`IsSceneObjectField` therefore returns false for `PPtr<$Graphic>` and `PPtr<$Selectable>`, so a null
`Button.targetGraphic` is classified as an asset reference rather than a scene-object reference.

**Build:** resolution by short name across loaded assemblies, with an unambiguity check. Two types
sharing a short name fail loud and located; never silently pick one.

**Accept when:** these warnings no longer appear for any UI component —
`Could not resolve component type 'Graphic' / 'Selectable' / 'Sprite' / 'Material'` — and a
`Button.targetGraphic`, null and assigned, classifies as a scene-object reference.

## C6 — the builder holds only what a user can set in the Inspector

A field a user cannot set is not author intent and must not reach builder source.

Unity exposes no API for "can a user edit this" — custom editors are opaque — so enforcement is a
curated exclusion list. Every entry must be justified by the rule above, not by taste.

**Build:** exclude `SpriteRenderer.m_WasSpriteAssigned` (no Inspector control; a side effect of
assigning a sprite) and `SpriteRenderer.m_Size` (derived from sprite bounds). Determine whether a
single Inspector control writes two serialized fields (`m_SortingLayer` + `m_SortingLayerID`, where
Canvas serializes only the ID) and exclude the redundant one. Proving that needs a second sorting
layer in `ProjectSettings/TagManager.asset`.

**Accept when:** a `SpriteRenderer` with an assigned sprite produces neither `m_WasSpriteAssigned`
nor `m_Size` in source, and one Inspector control yields at most one authored field.

## C7 — visibility of an unrepresentable field

A `UnityEvent` field cannot be represented in builder source: its serialized members
(`m_PersistentCalls`) have no compiling initializer form. Every UI component carrying one emits a
console NOTE and nothing else, so wiring a `Button.onClick` in the Inspector produces no code and the
scene edit is not synced. A console warning is easy to miss.

**Build:** decide and implement where an unrepresentable field surfaces — a conflict marker in the
source, where the author is reading, or console-only. Do not leave it emitting a warning by default.

Representing persistent calls is `specs/09`, not this spec.

**Accept when:** an author who wires an `onClick` in the Inspector finds out, at the place they are
working, that it did not reach their code.

## C8 — a no-op source edit issues a write op

A pure member reorder of a struct initializer, changing no value, produces
`Built in place: 1 plan op(s)` rather than zero. The following sync reports `Scene already matches
code`, so it converges and corrupts nothing.

**Build:** measure whether that op sets the scene dirty flag. If it does, suppress the op for an edit
that changes no value — otherwise every keystroke inside a struct initializer marks the scene dirty.
If it does not, close this with the measurement recorded and build nothing.

## Existing unvalidated code

`com.codescenes/Editor/` — `ComponentTypeResolver.cs`, `ObjectReferenceResolver.cs`,
`ComponentTypeNormalizer.cs`, `PrefabInstanceProbe.Overrides.cs`, `SerializedFieldBridge.cs`,
`SerializedFieldExclusions.cs`; `unity-gate/Assets/GateTests/` — `PPtrShortNameResolutionTests.cs`,
`InspectorVisibilityRuleTests.cs`.

This code implements C5 and C6 and is on `main`. The C5 work carries a GREEN validator verdict; the
C6 work does not — its behavioral deliverable was accepted without captured evidence — and neither
passed a bucket seam review. Re-validate both against the contracts above. Do not treat it as
settled.

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

- **Core:** short-name resolution is unambiguous; an ambiguous short name fails loud and located.
- **EditMode** (required — adapter changes): a real `Button.targetGraphic`, null and assigned,
  resolves as a scene-object reference with no unresolved-type warning; a `SpriteRenderer` with an
  assigned sprite produces no `m_WasSpriteAssigned` or `m_Size` in source; the round-trip suite above.
- **Live:** a real-editor pass over the same behaviors.
