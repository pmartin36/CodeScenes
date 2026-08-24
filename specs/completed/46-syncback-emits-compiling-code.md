# Spec 46: sync-back emits compiling C# — private members and forward declarations

Two scene→code emitter defects, both observed live in a one-shot authoring run (SceneBuilderOneShot,
Claude session `a6792c4f`), that each write **non-compiling** builder source. Together they caused the
agent to judge two-way sync "untenable for iterative authoring" and disable auto-sync, i.e. they
disabled the product's core mechanism. CLAUDE.md: "a write path that can emit non-compiling source is
a bug, not a style issue." This is the highest-priority item from that run.

## The measured defects

Reproduced during real authoring (auto-sync on), Console verbatim:

1. **Typed selector emitted for an inaccessible field.** For a MonoBehaviour field declared
   `[SerializeField] private`, the writer emitted `c.Set(r => r.field, ...)`, which cannot compile
   (a lambda cannot read a private member). Console:
   `Sync wrote Minigolf.cs: emitted builder source DOES NOT COMPILE (N error(s)). This is a bug in
   SceneBuilder's emission, not in your scene edit.` Workaround the author resorted to:
   `sed -i 's/\[SerializeField\] private /public /g'` across every gameplay script.

2. **A handle named before its own `var`.** Sync ordered a reference so a handle (a `canvas`) was used
   on a line ABOVE its `var canvas = ...`, emitting `CS0841` (use before declaration). This is the
   class `specs/completed/43-*.md` fixed for one emit path (declare-before-use); it **recurred** here
   on another, so 43's guarantee is not global.

Both rewrote the file on every build, so the loop the product exists to provide corrupted its own
source until sync was switched off.

## The fix

Owner: the scene→code emitter (`SceneBuilder.Core/Reconcile/` — `ComponentPatchApplier.cs`, the
member-set emission; `StatementPlacement.cs` for ordering).

- **Never emit a typed member selector for a field the builder cannot access.** When a serialized
  field is not public (private / `[SerializeField] private` / internal), emit the raw string form
  `c.Set("<serializedName>", ...)`, which always compiles. The typed selector is a public-field-only
  ergonomic. One shared decision in the emitter, so no author has to make fields public to keep sync.
- **Declare-before-use holds for every emitted reference.** Extend spec 43's `StatementPlacement`
  ordering to the recurring case (a handle referenced by an earlier-emitted statement), through the
  one placement mechanism, with a guard that fails when any emitted statement names a `var` declared
  on a later line.

## Accept when

A scene synced to code compiles (the gate's Roslyn compile assertion is green) when it has (a) a
MonoBehaviour with a `[SerializeField] private` field set from the scene, and (b) a component field
referencing a handle that would otherwise be declared later; and re-syncing the converged builder is
byte-stable (no repeated rewrite). A permanent guard test fails if the emitter writes a typed selector
for a non-public field or names a `var` before its declaration.
