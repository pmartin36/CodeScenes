# Spec 54: sync self-heals an authored typed selector on an inaccessible field to compiling source

An author writes the idiomatic Unity form — a `[SerializeField] private` field — and references it with the
TYPED selector `c.Set(r => r.myField, value)` in a builder. CodeScenes parses source (never compiles it),
so the selector is accepted at author time; code→scene apply WORKS (spec 47's `AuthoredPathResolver`
resolves a private serialized field). But scene→code sync RETAINS a typed selector referencing the private
member, which cannot compile (a lambda cannot read a private member, CS0122), and the product's own
`BuilderCompileCheck` logs `emitted builder source DOES NOT COMPILE`. Spec 46 fixed the emit side only for a
scene-ORIGINATED value change (downgrade to the raw-string form for non-public fields); an author-written
private selector whose value does NOT change is never rewritten, so it sits non-compiling forever. This is
the defect that forced the house one-shot to disable auto-sync — the flagship value prop (invisible two-way
sync) actively producing non-compiling code. CLAUDE.md: "a write path that can emit non-compiling source is
a bug, not a style issue."

Two emitters exhibit the same class; both are covered here.

## The measured defects

Both filed as open-defects during spec 46 (`docs/open-defects.md`), same class on two emitters:

1. **Component `.Set` — no self-heal without a value change.** An authored inaccessible typed selector in an
   already-converged builder produces no `PatchComponentField`: the field-reconcile value-equality gate
   (`SceneBuilder.Core/Reconcile/ComponentReconciler.cs:338-352`) `continue`s when the emittable source value
   equals the snapshot value and `AuthoredTextIsCurrent`, so nothing is emitted and sync never rewrites the
   selector. Spec 46's downgrade (`ComponentPatchApplier.ResolvePatchComponentField`,
   `ComponentPatchApplier.cs:208-227`) only runs when an edit IS produced (the value-diff branch,
   `:427-432`). Unchanged → skipped → `BuilderCompileCheck` stays red for that file.

2. **Prefab-instance `.Override` — the render has no accessibility check at all.**
   `.Override(e => e.Set(x => x.priv, v))` re-renders via `RenderOverrideSetCall`'s `member:` arm
   (`SceneBuilder.Core/Reconcile/SourcePatchApplier.Instances.cs:288-291`) as a typed selector
   `(T x) => x.<name>` unconditionally — the same CS0122 class, on the prefab-instance override path, which
   spec 46's accept-when (a plain component field) never exercised. `SourcePatchApplier.Apply` already holds
   the `inaccessibleMembers` index (`SourcePatchApplier.cs:34`) but never forwards it to this instance path.

The apply direction is confirmed working: `AuthoredPathResolver.ResolvePath`
(`com.codescenes/Editor/AuthoredPathResolver.cs:342`) resolves the `member:` sigil against a live
`SerializedObject` probe, explicitly handling `[SerializeField] private m_foo`. The defect is emit-only.

## The fix

The two emitters have no single shared caller (one is a rewrite-in-place applier, one is a string
renderer), so the shared thing is the **predicate + the invariant**, and one guard test enforces the
invariant across both. Reuse spec 46's form-choice decision; introduce no new one.

- **Component path — OWNER: the value-equality gate in `ComponentReconciler`
  (`ComponentReconciler.cs:338-352`).** Thread a trailing `InaccessibleMemberIndex? inaccessibleMembers`
  param (precedent: `memberSpellings` is already the last param at `:72`), built once in
  `Reconciler.Reconcile` from `actual.InaccessibleMembers` beside the existing
  `MemberSpellingIndex.Build(actual.MemberSpellings)` (`Reconciler.cs:63`) and passed at the
  `ReconcileComponents(...)` call (`Reconciler.cs:609`). At the gate, before the `continue` at `:351`, when
  the value is unchanged: if the field key starts with `member:` (the typed-selector sigil composed by
  `BuilderParser.MemberFieldKey`, `BuilderParser.cs:607-608`) AND
  `inaccessibleMembers.IsInaccessible(sourceComp.Type.FullName, <member name after the sigil>)`
  (`MemberSpelling.cs:88`), emit a value-preserving `PatchComponentField { Anchor = sourceComp.LogicalId,
  ValueSpan = fieldArgumentSpans[LogicalId][fieldKey], NewExpr = <the same RenderFieldValue construction the
  diff branch uses at :416-426> }`. That anchored edit routes into the EXISTING
  `ComponentPatchApplier.ResolvePatchComponentField` downgrade (`:208-227`), which rewrites arg0 to
  `SourceExpr.StringLiteral(member)`. The reconciler only supplies the trigger the diff gate withholds; the
  accessible-field path is untouched (the branch fires only on `member:` keys flagged inaccessible, so public
  fields still `continue`).

- **Override path — OWNER: `RenderOverrideSetCall` (`SourcePatchApplier.Instances.cs:284-295`).** Forward the
  `inaccessibleMembers` index `SourcePatchApplier.Apply` already holds (`:34`) into this renderer, and in the
  `member:` arm render the string form `Set<{TypeFullName}>("{name}", {valueText})` when
  `IsInaccessible(TypeFullName, name)`, instead of the `(T x) => x.<name>` selector. Same predicate, same
  string spelling as the component path.

- **String spelling is inherited, not reinvented.** The applier writes the string form from the selector
  identifier text (`ComponentPatchApplier.cs:216`) — `"myField"`, not a mangled `m_` path — which
  `AuthoredPathResolver.ResolvePath` resolves back to the serialized path. Spec 54 must use that same
  spelling on both paths; it introduces no new string form.

## Invariant and check

- **INVARIANT.** After any scene→code sync, no emitted or retained builder source contains a typed member
  selector (`sel => sel.M` on a component `.Set`, or `(T x) => x.M` on an override `.Set`) for a `(T, M)`
  the probe reports inaccessible. Convergence: after the first heal the source is
  `c.Set("myField", v)` (a verbatim string key on re-parse), so the `member:` trigger no longer matches, the
  equality gate holds, and sync is byte-stable — one extra sync to heal, stable thereafter.
- **CHECK that fails on bypass.** A Core guard test that runs reconcile+apply over a fixture authoring
  `c.Set(r => r.priv, v)` AND `.Override(e => e.Set(x => x.priv, v))` with `(T, priv)` in the
  `InaccessibleMemberIndex`, then scans the applied source and FAILS if any `.Set(<lambda>.priv, …)` survives
  on either path — so a future emitter that reintroduces a selector for an inaccessible member is caught. The
  guard covers both emitters, since they do not share a caller.

## Accept when

- **Core, diff-independent (both paths).** Given source authoring `c.Set(r => r.priv, v)` for a `(T, priv)`
  marked inaccessible and a snapshot whose value equals `v` (NO scene change), reconcile+apply rewrites it to
  `c.Set("priv", v)`; a second reconcile against the healed source produces zero edits (byte-stable). A
  parallel case for `.Override(e => e.Set(x => x.priv, v))` → `.Override(e => e.Set<T>("priv", v))` (or the
  shipped string-override form).
- **EditMode gate (the real Unity boundary).** Author `c.Set(r => r.privateField, v)` on a live component
  whose field is `[SerializeField] private`, build to scene, then sync back: `BuilderCompileCheck` reports
  ZERO errors (no `DOES NOT COMPILE` `Debug.LogError` at `SceneBuilderSync.cs:303`), the file now uses the
  string form, and a re-sync is byte-stable. This exercises the real `SerializedProperty`/`GlobalObjectId`
  path so the `InaccessibleMembers` set is produced by `SerializedFieldBridge`, not a fixture. Use a plain
  MonoBehaviour with a `[SerializeField] private` field — NOT TMP or any import-forcing component.
- **The guard is green** and the two open-defect entries (`docs/open-defects.md`, the low component-heal and
  med override-render entries) are removed.

## Out of scope

- **The author-time analyzer diagnostic** (an SB12xx warning in the IDE for a typed selector over an
  inaccessible serialized member). Feasible — the analyzer has a semantic model
  (`CodeScenes.Analyzers/UnityEventAnalysis.cs:46`) — but a separate follow-up. The emit-side self-heal is
  the guarantee: the product must never emit non-compiling source regardless of author diligence.
- **`[FormerlySerializedAs]` resolution** (`docs/open-defects.md`, a separate resolver gap).
- **No change to spec 46's form-choice or the string spelling** — both are reused as-is.
