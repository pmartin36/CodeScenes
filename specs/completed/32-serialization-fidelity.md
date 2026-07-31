# Spec 32 — Serialization boundary fidelity

> ## SCOPE NARROWED 2026-07-31 — this spec now covers C1-C4 only
> Delivered and committed: **Owner 1**, the per-type default template (`4611d92`, C2 + C3), and
> **Owner 2**, the value representation contract (`72c8c78`, C1 + C4), plus a structural
> nested-value fix landing `SceneBuilder.Core/Model/ValueWalk.cs` as the single walk every value
> path uses. Gate at `passed=517 failed=0`.
>
> **C5 (PPtr short-name resolution) and C6 (the Inspector-visibility rule) moved to `specs/33`**,
> together with the bidirectional round-trip suite that was going to prove them. Partial work on
> both sits in `795eba2`, explicitly labelled unvalidated — b3-t1 reached GREEN, b3-t2 escalated
> for a behavioral deliverable with no captured evidence, and the bucket never passed a scope
> review.
>
> **What that means for confidence:** C1-C4 are gate-verified. Read the "Verification status"
> section at the bottom before assuming more than that.

> Written from a live-editor sweep of the real UI surface (2026-07-30). Every defect below was
> MEASURED in a running editor against `6c800e6`, not inferred. The batchmode gate was green at
> `passed=458` throughout: none of these are visible to it.

## Why this exists

M-UI (spec 13) shipped RectTransform geometry. The moment live testing touched the rest of a real UI
— an `Image` with a sprite, a `Button` with colors, a `Canvas` with a render mode — six distinct
defects surfaced. They are not UI bugs. They are defects in the **serialization boundary**: what the
reader is allowed to see, how a value is represented, and what the writer emits. UI is simply where
they concentrate, because UI is dense in native enums, asset references, nested structs and
custom-editor-drawn fields.

Two of them (C2, C4) mean an ordinary user session ends with a **divergent scene** or a
**permanently non-compiling builder file**. Those are product-stopping.

## Defect classes

### C2 — Resetting a field to its type default is silently dropped (scene→code) **[SEVERITY: TOP]**
`SerializedFieldBridge.ReadComponent` prunes fields whose value equals the per-type default (via
`GetDefaultFieldMap`) **before the differ ever sees them**. So clearing a sprite, setting a colour
back to white, or setting `Image.type` back to `Simple` produces `Scene already matches code —
nothing to sync`, and the stale value stays in source. The scene and the code diverge with **no
conflict marker and no error**.

Measured: live `m_Sprite = null`, `m_Material = null`, `m_Color = [1,1,1,1]`, `m_Type = "Simple"`;
source still read `Asset("Assets/LiveVerifyFixtures/LVSprite.png", "LVSprite")` and `Tiled`. Control
in the same session: a non-default colour synced in one cycle, so the pipeline was live.

**Contract:** pruning to defaults is correct for deciding what to *author* (Paul's non-default rule).
It is wrong as a *read* filter, because "the user reset this to default" is a real edit. The reader
must report the field; the decision to omit it from source belongs to the emit side, and a field
present in source whose live value returns to default must be **removed from source**, not ignored.

### C4 — Nested struct emission produces non-compiling C# **[SEVERITY: TOP]**
`ColorBlock` emits `new UnityEngine.UI.ColorBlock { m_NormalColor = ..., m_ColorMultiplier = ... }`
→ 7 × `CS0122: inaccessible due to its protection level`. `FontData` emits `m_Font`, `m_FontSize`…
→ 12 × `CS0117: does not contain a definition for`. The serialized field names are not the public
member names.

A user touching a Button and a Text reaches a **steady state of 19 compile errors** in their builder.
Sync keeps working (CodeScenes parses its own grammar, not C#), so the damage is the IDE, IntelliSense
and the `SceneBuilder.Validate` gate. This violates the standing rule that a write path which can emit
non-compiling source is a bug.

Secondary: it emits **all** members when one changed (7 for ColorBlock, 12 for FontData).

**Contract:** emit the public member spelling, or the struct's own construction form; and emit only
changed members. Where no compiling form exists, fail loud and located rather than writing broken C#.

### C1 — Native enums cannot round-trip in typed form
The reader (`SerializedFieldBridge.ReadEnum`) falls back to `Primitive.Int` when no managed
`FieldInfo` backs the path. The writer (`WriteEnum`) matches the authored member against
`SerializedProperty.enumNames`, which for a **native** enum are display strings (`"Screen Space -
Camera"`), so `ScreenSpaceCamera` never matches.

Measured, authoring `c.Set(x => x.renderMode, RenderMode.ScreenSpaceCamera)`:
1. warning `Enum member 'ScreenSpaceCamera' not found on 'm_RenderMode'`, the write is **refused**,
   leaving the canvas at a value the author never asked for;
2. the next sync **clobbers the typed text** to `c.Set(x => x.renderMode, 0)`;
3. that emitted form does not compile — `CS1660`, because `Set(x => …, 0)` binds to `Set(string, object)`.

Affects `Canvas.renderMode`, `Rigidbody.constraints` and every native enum/flags field. Managed enums
(`Image.Type`, `Selectable.Transition`, `CanvasScaler.ScaleMode`) already round-trip correctly.

**Contract:** resolve the enum type for native paths (Unity's convention maps `m_RenderMode` →
`Canvas.renderMode`, so a `PropertyInfo` lookup recovers it) and match on member NAME, not display
string. Typed and string forms must both round-trip, and neither may rewrite the other's spelling.
Paul: **being able to set screen space / camera / world space is a first-class requirement.**

### C3 — Per-type defaults come from `AddComponent`, not the editor's Reset path
`GetDefaultFieldMap` builds its template with `new GameObject{hideFlags=HideAndDontSave}
.AddComponent(type)`. That is the raw runtime path; Unity's editor uses Reset/`ObjectFactory`, which is
why the menu gives Screen Space Overlay while `AddComponent<Canvas>` gives World Space.

Consequence: a Canvas authored as `.Component<Canvas>()` is created World Space, AND — because that is
also the pruning template (C2) — World Space is the one render mode that can never sync back.
Measured twice (`1→2`, `0→2`): live `"World Space"`, console `Scene already matches code`.

**Contract:** the default template must match what the editor produces. Whether CodeScenes should
additionally nudge the author (analyzer `SB1107`, "this Canvas authors no render mode") is a separate
product call.

### C5 — PPtr type resolution misses every namespaced type
`ObjectReferenceResolver.ExpectedRefType` parses `PPtr<$X>` to the bare token `X` and hands it to
`ComponentTypeResolver.Resolve`, which matches on `Type.FullName`. `Sprite` never equals
`UnityEngine.Sprite`. Only `GameObject` is special-cased; it works today for M5 user MonoBehaviours
only because those have no namespace.

Emits `Could not resolve component type 'Sprite' / 'Material' / 'Graphic' / 'Selectable'`. Concrete
consequence: a null `Button.targetGraphic` is classified as an asset ref rather than a scene-object
ref. Not yet observed producing a wrong value, because C2 hides it.

**Contract:** resolve by short name across loaded assemblies with an unambiguity check, not by
`FullName` equality.

### C6 — Fields with no Inspector control are surfaced as authored data
**Paul's rule: only surface what a user can set in the Inspector.** Violations found:
`SpriteRenderer.m_WasSpriteAssigned` (no control at all) and `m_Size` (derived from sprite bounds).
Structurally present but not demonstrated: one Inspector control writing two serialized fields
(`m_SortingLayer` + `m_SortingLayerID` on SpriteRenderer; Canvas serializes only the ID).

**Contract:** spec 31 documented the exclusion *mechanism*; this records the *rule* that governs it.
A field a user cannot set is not author intent and must not reach builder source.

## Out of scope
- `onClick` / UnityEvent wiring — that is M8 (spec 09).
- TMP — not installed in the test project.
- Proving the dual sorting-layer emission (needs a second sorting layer in `TagManager.asset`).

## DECOMPOSITION CONSTRAINT (learned from M-UI — do not ignore)

These six classes are not six independent bugs. They collapse onto **two shared mechanisms**, and each
mechanism gets **ONE owning task with ONE shared implementation that every other task calls.** Do NOT
restate either as per-task guidance.

**Owner 1 — the per-type default template.** `GetDefaultFieldMap` is used in two places today and is
wrong in both roles. C2 and C3 are the same object seen twice: C3 is the template being built from the
wrong construction path, C2 is that template being used as a *read* filter when it should only inform
the *emit* side. One task owns the template: build it the way the editor does, expose it to
`PlanExecutor` creation and to emit-side omission, and remove it from the read path. C2 and C3 are then
consequences, not separate fixes.

**Owner 2 — the value representation contract.** C1 and C4 are both read and emit disagreeing about
what a value *is*. Today `ReadEnum`, `WriteEnum` and the source emitter each decide independently, so a
native enum reads as `Int`, writes by display-string match, and emits text that binds to the wrong
overload. One task owns the round trip for a serialized property: what `ValueNode` it becomes, what
source text that emits, and what parsing that text yields — with the invariant that **read → model →
emit → parse → write returns the original value and compiles.** C1 and C4 are then instances.

C5 (type resolution) and C6 (the visibility rule) are genuinely independent and can be their own tasks.

M-UI stated its invariant as guidance across six tasks; the seam pass kept finding the one that forgot,
re-opened committed buckets, and the milestone ran ~22 hours and ~13M tokens. That is what this
constraint exists to prevent.

## Suggested order
C2 and C4 first (divergent scene, broken builder file), then C1 and C3 (both Canvas-facing and
entangled through the default template), then C5 and C6.

## Verification status (read before trusting this spec)

**C1-C4 are GATE-VERIFIED, not live-verified.** The gate stands at `passed=517 failed=0`, every
bucket-b2 test survived the structural nested-value refactor unweakened, and each fix was
mutation-checked. That is strong evidence the code does what its tests say.

It is NOT evidence the product works, and this spec exists precisely because that distinction has
bitten twice: all six defect classes here were found in a live editor while the batchmode gate sat
green at 458 tests, and spec 29's multi-scene work was gate-green with auto-sync completely dead.

The live sweep that would close the gap is `specs/33`'s round-trip proof suite. Until it runs, the
honest claim for C1-C4 is "tests pass," not "works."

Known and accepted at close:
- Native enum fields whose backing public member no reflective rule can name (`m_CastShadows`,
  `m_MotionVectors`, `m_AdditionalShaderChannelsFlag`, `m_SpriteTileMode`, `m_Interpolate`) still
  round-trip as raw ints. Values converge correctly; only C1's typed spelling is absent.
- A struct whose representable members return to default while an unrepresentable member still
  differs reports on every sync. Console noise, not file churn — fail-loud beating silent loss. If it
  proves annoying it wants once-per-session suppression.
- `ComponentDefaultOmission.Index` probes via `FieldMap.TryGetValue`, a linear scan, where the
  blueprint specified an O(1) dictionary built once per reconcile. Correctness is unaffected.
- A `// CONFLICT:` marker writes a U+2014 em dash into the user's builder `.cs`
  (`SceneBuilderSync.Conflicts.cs:267`), violating the repo's ASCII-in-plain-text rule.

## Test plan
- **Core:** default-reset produces a removal edit; nested-struct emission compiles and is minimal;
  native enum round-trips typed and string forms; short-name type resolution is unambiguous.
- **EditMode** (required, adapter changes): each class exercised against real components — a real
  sprite cleared, a real `ColorBlock` edited, a real Canvas moved through all three render modes, a
  Canvas created from code asserted Screen Space Overlay.
- **Live:** re-run the UI sweep. Gate-green is not sufficient for any of these; all six were invisible
  to a 458-test gate.
