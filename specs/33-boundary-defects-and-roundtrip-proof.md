# Spec 33 — Boundary defects (C5, C6) and bidirectional round-trip proof

> Split out of `specs/32-serialization-fidelity.md` on 2026-07-31. Spec 32 delivered its two owners
> (the per-type default template, and the value representation contract) and was closed there so the
> work could land; these two independent defects plus the proof suite carry on here.
>
> **Partial work already exists in `795eba2`, and it is NOT validated.** b3-t1 (C5) reached a GREEN
> validator verdict. b3-t2 (C6) was ESCALATED because its behavioral deliverable went green with no
> captured evidence. Neither passed a bucket scope review. Treat that commit as a starting point to
> verify, not as done work to build on.

## C5 — PPtr type resolution misses every namespaced type

`ObjectReferenceResolver.ExpectedRefType` parses a `SerializedProperty.type` of the form `PPtr<$X>`
down to the bare token `X`, then hands it to `ComponentTypeResolver.Resolve`, which matches on
`Type.FullName`. `Sprite` never equals `UnityEngine.Sprite`; `Graphic` never equals
`UnityEngine.UI.Graphic`. Only `GameObject` is special-cased. It happens to work today for M5 user
MonoBehaviours purely because those have no namespace.

Observed live, verbatim, once each per domain:
```
[SceneBuilder] Could not resolve component type 'Graphic'.
[SceneBuilder] Could not resolve component type 'Selectable'.
[SceneBuilder] Could not resolve component type 'Sprite'.
[SceneBuilder] Could not resolve component type 'Material'.
```

Concrete consequence: `IsSceneObjectField` returns false for `PPtr<$Graphic>` / `PPtr<$Selectable>`,
so a null `Button.targetGraphic` is classified as an asset reference rather than a scene-object
reference. Not yet observed producing a wrong *value*, because spec 32's C2 defect was masking it —
which means this needs re-measuring now that C2 is fixed.

**Contract:** resolve by short name across loaded assemblies, with an explicit unambiguity check.
Two types sharing a short name must fail loud and located, never silently pick one.

## C6 — Fields with no Inspector control are surfaced as authored data

**Paul's rule (2026-07-30), governing:** *the builder holds only what a user can set in the
Inspector.* Spec 31 documented the exclusion *mechanism* (name lists); this records the *rule* that
decides what belongs in them, which is what keeps future additions correct.

Known violations:
- `SpriteRenderer.m_WasSpriteAssigned` — no Inspector control at all; it flips true as a side effect
  of assigning a sprite.
- `SpriteRenderer.m_Size` — derived from the sprite bounds, not authored.
- Suspected, not yet demonstrated: one Inspector control writing two serialized fields
  (`m_SortingLayer` + `m_SortingLayerID`). Proving it needs a second sorting layer in
  `ProjectSettings/TagManager.asset`, which the earlier sweep declined to add to a shared project.

**Contract:** a field a user cannot set is not author intent and must not reach builder source.
There is no Unity API for "can a user edit this" (custom editors are opaque), so enforcement is a
curated exclusion list — but the list must be justified by the rule, not by taste.

## C7 — UnityEvent fields are unrepresentable and only warn

Found during spec 32's live verification (2026-07-31). Every UI component carrying a UnityEvent emits
a NOTE and nothing else, verbatim:

```
[CodeScenes] NOTE on 'btn/UnityEngine.UI.Button#0': Cannot represent field 'm_OnClick' of
'UnityEngine.UI.Button' ... its serialized members (m_PersistentCalls) have no compiling initializer
form, so no value for this field can be written to code and a scene edit to it is not synced.
```

Also fires for `m_OnCullStateChanged` on `Image` and on `Text`. The message is honest and this is not
a regression, but the user-visible consequence is: **wire a `Button.onClick` in the Inspector and it
produces no code, with the edit silently not synced.** A console warning is easy to miss.

Representing persistent calls is M8's job (`specs/09`), NOT this spec's. What belongs here is the
visibility question: should an unrepresentable field surface as a conflict marker in the source, where
the author is actually looking, rather than only in the console? Decide, do not silently keep warning.

## C8 — a semantically no-op source edit still issues a scene write op

Found in the same sweep. After a pure member REORDER of a struct initializer (no value changed), the
code→scene watcher logged `Built in place: 3 object(s), 1 plan op(s)` — not zero — even though the
immediately following Sync reported `Scene already matches code`. Nothing was corrupted and it
converges.

**Unmeasured and worth measuring first:** whether that op dirties the scene. If it does, every
keystroke inside a struct initializer marks the scene dirty, which is a real annoyance in the
auto-sync loop. Measure the dirty flag before deciding whether it needs fixing.

## The round-trip proof suite

Spec 32's C1-C4 WERE live-verified on 2026-07-31, but only as a **smoke test of each class on its
archetype** — not a matrix. The honest claim from that sweep is "the four fixes work on the case we
tried." This suite closes the difference. Do NOT restate it as "cross-class coverage" and leave the
matrix to whoever builds it; the specific holes are enumerated below because they are what the sweep
did not touch.

**Already measured live, so NOT in scope here:** simple typed enums (`Canvas.renderMode`), FLAGS
enums (`Rigidbody.constraints` — multi-bit, out-of-order authoring, the composite `FreezeAll` member,
and zero-bit removal, all round-tripping typed with authored order preserved and no churn), three
reset-to-default cases, the Canvas default plus all three render modes, and `ColorBlock`/`FontData`
emission with a compile check, convergence, hand-reorder and partial initializer.

**Required coverage of the suite this spec BUILDS:**

1. **Components beyond Canvas / Image / Button / Text / Rigidbody** — at minimum `Camera`,
   `MeshRenderer`, `SpriteRenderer`, `Light`.
2. **A struct nested inside a struct.** `ValueWalk` exists precisely for recursion depth and nothing
   deeper than `ColorBlock` and its members has live evidence. This is the shared walk's whole reason
   to exist, so the suite must reach at least two levels.
3. **Prefab-instance context.** UI inside a prefab instance reads through `PrefabInstanceProbe`, a
   separate path from the plain reader.
4. **Lists of structs.** `ExcludeUnrepresentable` deliberately does not filter list items (carried as
   an explicit `InList` flag rather than an unstated omission); the suite must pin that choice.

Every one of spec 32's six defect classes was invisible to a 458-test batchmode gate, so this suite's
live pass is mandatory, not optional. Gate-green has proven insufficient on this codebase twice.

## Starting-state caveat — read before trusting `main`

`795eba2` put b3's partial work on `main` and it is UNVALIDATED: b3-t1 (C5) reached GREEN, b3-t2 (C6)
ESCALATED for a behavioral deliverable with no captured evidence, and the bucket never ran a scope
review. That code is live in the built DLLs.

Two consequences:
- Spec 32's live verification ran on a tree that **already contained** this unvalidated C5/C6 work.
  Nothing was broken by it (the gate is green at 517 including it), but "32 is live-verified" is
  strictly a statement about that tree, not about 32's changes in isolation.
- This spec must **re-validate** `795eba2`, not build on top of it as if settled. Treat b3-t2's
  Inspector-rule claim in particular as unproven until it has captured evidence.

## DECOMPOSITION CONSTRAINT (carried forward — this is what spec 32 cost)

Spec 32's bucket b2 took **six passes on one task** because "recursion into nested values" was a
cross-cutting invariant that no single task owned, so non-recursing sites were found one at a time:
the enum normalizer had no `Nested` arm, `Complete` didn't recurse while `Reduce` did, the
comparison path re-raised an unconvergeable conflict every sync. Spec 32's constraint named "the
value representation contract" as the owner, which was right in kind and wrong in specifics.

**Before decomposing this spec, name the cross-cutting invariant precisely and give it ONE owning
task with ONE shared implementation every other task calls.** For C5 the candidate is type
resolution itself: one resolver every call site uses, rather than each site parsing and matching its
own way. Do not restate an invariant as per-task guidance.

`SceneBuilder.Core/Model/ValueWalk.cs` (from spec 32) is the model to follow: a pass declares what it
does to a node and how it descends, so *forgetting to recurse is not expressible*.

## Test plan
- **Core:** short-name resolution is unambiguous, and an ambiguous short name fails loud and located.
- **EditMode (required, adapter changes):** a real `Button.targetGraphic` (null and assigned) resolves
  as a scene-object reference with no `Could not resolve component type` warning; a `SpriteRenderer`
  with an assigned sprite produces no `m_WasSpriteAssigned` or `m_Size` in source.
- **Live:** the sweep above. Not optional.
