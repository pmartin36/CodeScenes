# Spec 54: sync emits a typed member selector only on positive proof of a public C# member

scene→code sync writes or retains a typed member selector — `c.Set(r => r.M, v)` on a component, or
`.Override(e => e.Set(x => x.M, v))` on a prefab instance — for members it cannot prove are public C#
members, so the emitted builder does not compile and the product flags its own output
(`emitted builder source DOES NOT COMPILE`). Observed live in the house one-shot as two `CS1061`
(`'PlayerController' does not contain a definition for <member>`) — a real non-compiling EMISSION, distinct
from a "Sync failed" abort. CLAUDE.md: "a write path that can emit non-compiling source is a bug, not a
style issue." The approved fix: **emit a typed selector ONLY when the adapter can prove the member is a
public, existing, type-consistent serialized member of the component's declaring type; otherwise emit the
always-compiling string form `c.Set("M", v)`.** This subsumes spec 46's private-field case and additionally
catches the native / not-a-public-member case the current guard is structurally blind to.

## The measured defect

Today the guard is a BLACKLIST, `InaccessibleMemberIndex.IsInaccessible`
(`SceneBuilder.Core/Model/MemberSpelling.cs:88`), built by `SerializedMemberMap.IsInaccessibleViaSelector`
(`com.codescenes/Editor/SerializedMemberMap.cs:103-105`):

```csharp
GetFieldRecursive(componentType, serializedPath) != null      // requires a MANAGED FieldInfo
    && !TryPublicMemberName(componentType, serializedPath, out _);
```

It can only flag a member that is a MANAGED serialized field with no public spelling (the private case,
CS0122). It is blind by construction to:
- **Native serialized fields** (e.g. `Rigidbody.m_Mass`): no managed `FieldInfo`, so never flagged. A
  retained/authored `r => r.m_Mass` resolves at load (exact `FindProperty` hit,
  `AuthoredPathResolver.cs:344`), is not in the blacklist, and is retained/re-rendered → **CS1061**. This is
  the reachable emission defect.
- **Any serialized property whose name is not a public C# member** of the current type.

Spec 46 downgrades to the string form only on the VALUE-CHANGE branch
(`ComponentPatchApplier.ResolvePatchComponentField`, `ComponentPatchApplier.cs:205-227`, reached only when a
`PatchComponentField` is produced) and only for `InaccessibleMemberIndex`-flagged members — so an UNCHANGED
selector, and every native/not-a-public-member selector, slip through. The prefab-override render
`RenderOverrideSetCall` (`SourcePatchApplier.Instances.cs:288-291`) emits the `member:` selector
**unconditionally**, with no accessibility check at all.

Out of this spec (separate failure mode): a truly REMOVED member — `member:X` with no live serialized
property — throws in `AuthoredPathResolver.ResolvePath` (`:430-432`), caught by `SceneBuilderSync`
(`:112-115`), which aborts the sync and writes nothing. That is a different bug (a resolver-side abort, no
serialized path to downgrade against) and its own follow-up.

## The fix

The two emit sites share no caller, so the shared thing is the **predicate + the invariant**; one guard
test enforces both.

**The positive-proof whitelist (Core cannot reflect — produced adapter-side per `00-foundation.md`).** A new
per-type set `SafeSelectableMembers` = the keys of `SerializedMemberMap`'s `PublicToSerialized`
(`SerializedMemberMap.cs:279-323`): every public field name, plus a `[SerializeField] private` field's
conventional `m_Xxx→xxx` property spelling only when that property is non-obsolete, non-indexed, has a public
setter, and `PropertyType == FieldType` (`:294-303`). Membership means exactly "`r => r.M` compiles as a
public serialized member of T"; native `m_` fields and names the type does not declare are absent by
construction, and the type-consistency the selector needs is already enforced by the map (name +
public-readability suffices; no separate value-type check at the emit site). The public-setter requirement
is deliberately conservative — a getter-only public alias downgrades to string form, which is always safe.

**Produced and threaded like the existing signals.** `SerializedFieldBridge.CollectFields`
(`com.codescenes/Editor/SerializedFieldBridge.cs:97-179`) registers the owner type's safe member names into a
`ComponentDefaultTemplate` registry, mirroring `RegisterInaccessibleMembers`
(`ComponentDefaultTemplate.cs:187-203`), gathered from the same read so the signal cannot diverge from what
was read. `SceneSnapshotReader.FromRoots` (`SceneSnapshotReader.cs:90-129`) drains it into a new
`SceneSnapshot` array beside `InaccessibleMembers` (`SceneSnapshot.cs:22-28`); a `SafeMemberIndex` is built
once in `Reconciler.Reconcile` beside `MemberSpellingIndex.Build(actual.MemberSpellings)` (`Reconciler.cs:63`)
and threaded through `ReconcileComponents` (`Reconciler.cs:609`) to the two emit sites — including the
override plumbing gap where `SourcePatchApplier.Apply` currently forwards `inaccessibleMembers` only to
`ResolvePatchComponentField` (`SourcePatchApplier.cs:119`), not to the instance path (`:75`).

**Both emitters invert the predicate.**
- **Component OWNER: `ComponentPatchApplier.ResolvePatchComponentField` (`:205-227`).** Retain the typed
  selector iff `SafeMemberIndex.IsSafe(typeFullName, M)`; otherwise take the existing string-form arm
  (`SourceExpr.StringLiteral(M)`, `:216`). Same rewrite mechanism, whitelist predicate.
- **Override OWNER: `RenderOverrideSetCall` `member:` arm (`SourcePatchApplier.Instances.cs:288-291`).** Render
  `Set<{T}>("{M}", {value})` (the string form the applier already uses) unless `IsSafe(T, M)`.
- **Unchanged-selector self-heal.** A converged builder whose authored selector's value did not change never
  reaches the value-change downgrade. Emit a diff-independent heal so an unchanged NOT-safe selector is
  rewritten to the string form on the next sync (the value-equality gate,
  `ComponentReconciler.cs:338-352`, is where the field is currently skipped). This heal MUST fire only for a
  field actually authored as a selector over a not-safe member — never for a field already in string form —
  so an already-healed file produces no edit (see the byte-stability + no-convergence-defect accept-when,
  which is the check that forces this).

**String spelling is inherited, not reinvented** — the identifier text `AuthoredPathResolver.ResolvePath`
resolves back (`ComponentPatchApplier.cs:216`). `InaccessibleMemberIndex` (blacklist ⊂ "not in whitelist")
is subsumed and retired. The append/introduce path already emits string form only
(`ComponentPatchApplier.cs:414-417`) — no selector to gate there, leave it.

## Invariant and check

- **INVARIANT.** After any scene→code sync, no emitted or retained builder source contains a typed member
  selector (`sel => sel.M` on a component `.Set`, or `(T x) => x.M` on an override `.Set`) for a `(T, M)` the
  adapter did not prove is a safe public selectable member. A public C# field authored as a selector is
  retained as a selector (no downgrade); a native/private/not-a-public-member selector becomes the string
  form.
- **CHECK.** A Core guard test runs reconcile+apply over a fixture authoring `c.Set(r => r.M, v)` AND
  `.Override(e => e.Set(x => x.M, v))` for a `(T, M)` absent from `SafeMemberIndex`, then scans the applied
  source and FAILS if any `.Set(<lambda>.M, …)` survives on either path (covers both emitters, which share no
  caller). A companion case asserts a `(T, M)` that IS in `SafeMemberIndex` (a public field) is retained as a
  selector, so the fix does not over-downgrade.

## Accept when

- **Core, diff-independent (both paths).** Given source authoring `c.Set(r => r.M, v)` for a `(T, M)` NOT in
  `SafeMemberIndex` and a snapshot whose value equals `v` (NO scene change), reconcile+apply rewrites it to
  `c.Set("M", v)`, and a second reconcile against the healed source produces ZERO edits (byte-stable). A
  parallel case for `.Override(e => e.Set(x => x.M, v))`. A public-field selector `c.Set(r => r.PublicX, v)`
  is retained unchanged (byte-stable, no downgrade).
- **No churn / convergence-defect.** Re-syncing an already-healed (string-form) builder produces zero patch
  edits and logs NO `[SceneBuilder] Convergence defect` error (`SceneBuilderSync.cs:314`) — the self-heal
  must not emit a byte-identical no-op edit for a field already in string form. (This is the acceptance
  criterion that forces the heal to fire only on an actual not-safe selector.)
- **EditMode gate (the real Unity boundary).** On a live component, author a typed selector over a member
  that is a serialized property but NOT a public C# member — a native `m_`-prefixed field (the CS1061 case)
  AND, separately, a `[SerializeField] private` field (the CS0122 case) — build to scene, then sync back:
  `BuilderCompileCheck.CheckAndReport` (`SceneBuilderSync.cs:303`) reports ZERO errors (no
  `DOES NOT COMPILE`), the file now uses the string form, and a re-sync is byte-stable. Repeat for a
  prefab-instance `.Override`. Exercises the real `SerializedProperty` path so `SafeSelectableMembers` is
  produced by `SerializedFieldBridge`, not a fixture. Use a plain MonoBehaviour / a stock component —
  never TMP or any import-forcing component.
- **The guard is green** and the two open-defect entries (`docs/open-defects.md`, the low unchanged-heal and
  med override-render entries) are removed.

## Out of scope

- **The removed-member load-time throw.** `member:X` for a member the type no longer declares throws in
  `AuthoredPathResolver.ResolvePath` (`:430-432`) and aborts sync — a distinct resolver-side change with no
  serialized path to downgrade against. Separate follow-up.
- **The author-time analyzer diagnostic** (an SB12xx warning for a typed selector over a non-public
  serialized member) — feasible via `CodeScenes.Analyzers`, but a separate follow-up; the emit-side heal is
  the guarantee.
- **`[FormerlySerializedAs]` resolution** (`docs/open-defects.md`, a separate resolver gap).
- **No change to spec 46's string spelling** — reused as-is; the blacklist predicate is generalized to the
  whitelist, not replaced with a new string form.
