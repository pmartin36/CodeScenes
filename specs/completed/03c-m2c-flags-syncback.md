# M2c — Flag sync-back (tag / layer / active / static, scene→code)

Extends M2 (which synced move/rename/reparent/reorder of EXISTING objects) to the four GameObject
**flags** — `Tag`, `Layer`, `Active`, `IsStatic`. M1 already writes these code→scene
(`SetTag`/`SetLayer`/`SetActive`/`SetStatic`), and M2 explicitly synced only transform/name/parent/order,
so today changing an object's Active/Tag/Layer/Static in the editor has **no scene→code owner**. M2c
closes that gap. Binds to `00-foundation.md` and reuses M2's `SourceEdit`/`SourcePatch`/`Reconcile`/
`Anchors` and the M2b `SourceExpr` literal helpers. This is an EXTENSION of existing Core — do not
rebuild M0–M2/M2b; keep all existing tests green.

### Additions to the contract
Three new `SourceEdit` variants (all span-local, formatting-preserving; they patch/insert **within one
existing anchored statement's fluent chain**, never a whole statement):
- **`PatchFlagArgument { Anchor, Flag, NewExpr }`** — rewrites the single argument of an EXISTING
  `.Tag(...)` / `.Layer(...)` / `.Active(...)` call in the anchored statement's chain. `Flag ∈ {Tag,
  Layer, Active}` (Static has no argument). Mirrors M2's `PatchArgument` / M3's value-argument patch.
- **`IntroduceFlagCall { Anchor, Flag, ArgExpr? }`** — appends a flag call onto the END of the anchored
  statement's fluent chain (immediately before the terminating `;`) when that call is ABSENT:
  `.Tag("X")` / `.Layer(n)` / `.Active(false)` (`ArgExpr` carries the literal), or `.Static()`
  (`ArgExpr == null`; no-arg). Mirrors M3's field-introduction / SourcePatchApplier statement-argument
  insertion.
- **`RemoveFlagCall { Anchor, Flag }`** — removes an existing `.Static()` call (Static reverting to
  `false` = absence of the call) and, symmetrically, a now-redundant explicit `.Active(...)`/`.Tag(...)`/
  `.Layer(...)` call whose scene value has returned to the type default (see default-value handling).

No new §3 value types. `SceneSnapshot`/`SnapshotNode` already carry `Tag`, `Layer`, `Active`,
`IsStatic` (§3 `GameObjectNode` shape, mirrored on the snapshot); `SceneModel`, `ChangeSet`,
`ReconcileResult`, `Conflict` are used as already typed.

## Goal
When a user toggles **Active**, retags, changes **Layer**, or flips **Static** on an EXISTING mapped
GameObject in Unity, a fresh full snapshot is diffed against the parsed model on `GlobalObjectId`, and
the builder `.cs` source is patched — formatting-preserving — so the code again matches the scene:
either by rewriting the argument of an existing `.Tag/.Layer/.Active` call, or by introducing the
missing flag call onto the object's statement (e.g. the object had no `.Active()` and the user
deactivated it → `.Active(false)` is appended). Idempotent: a second Sync of an unchanged scene is a
no-op.

## In scope
- **Detect** a changed `Tag` / `Layer` / `Active` / `IsStatic` on an EXISTING mapped GameObject
  (`GlobalObjectId` present in the IdentityMap) between the parsed `SceneModel` and the `SceneSnapshot`,
  and lower each difference to one flag `SourceEdit`.
- **Patch existing:** when the object's statement already carries the flag call, rewrite only its
  argument (`.Tag("Old")→.Tag("New")`, `.Layer(8)→.Layer(3)`, `.Active(true)→.Active(false)`).
- **Introduce when absent:** when the call is missing and the scene value is **non-default**, append the
  flag call onto the statement's chain (`.Active(false)`, `.Tag("Enemy")`, `.Layer(6)`, `.Static()`).
- **Remove when reverting to default (Static, and redundant explicit calls):** `IsStatic` false with a
  present `.Static()` → `RemoveFlagCall`; an explicit call whose scene value returns to the type default
  → `RemoveFlagCall` (keeps source clean and second-Sync idempotent).
- **Default-value handling:** the type defaults are `Tag="Untagged"`, `Layer=0`, `Active=true`,
  `IsStatic=false` (§3). A parsed statement with no flag call is modeled at the default; a diff is
  emitted only when snapshot ≠ model. INTRODUCE never emits a default-valued call (never `.Active(true)`,
  never `.Static()` for a non-static object, never `.Tag("Untagged")`) — a default scene value with an
  absent call is already agreement, so it is a no-op.
- Coexists with M2's move/rename/reparent/reorder and M2b's create/delete edits in the SAME Reconcile
  (one snapshot, one `ReconcileResult`).

## Out of scope
- **Created / deleted** objects (M2b). M2b's `AppendStatement` already carries `Active/Tag/Layer/IsStatic`
  when non-default at creation, and delete removes the whole statement; M2c does NOT re-emit flag edits
  for an object whose `GlobalObjectId` is not yet in the IdentityMap (unmapped) — that avoids
  double-emission. M2c is strictly EDIT-of-existing-mapped.
- Component/field flags, asset/cross-object refs, prefab flags (M3+/M4+/M6+).
- Transform / name / parent / order (M2) — unchanged.
- Robust both-directions merge / self-event suppression (M7) — M2c writes the patch directly.

## Core deliverables

### Types added/changed (referencing §3)
- New `SourceEdit` records: `PatchFlagArgument`, `IntroduceFlagCall`, `RemoveFlagCall` (flagged above).
- No `SceneModel` / `SceneSnapshot` shape change: `GameObjectNode` and its snapshot mirror already
  carry `Tag`, `Layer`, `Active`, `IsStatic` (§3). Core's Roslyn parse already lowers `.Tag(...)`,
  `.Layer(...)`, `.Active(...)`, `.Static()` into these fields (M1); M2c consumes them.
- No `ReconcileResult` shape change: flag edits are ordinary `SourceEdit`s in the existing
  `Patch.Edits`; no map delta (mapped objects only).

### Functions / behaviors (each a testable contract)
- **Reconcile — patch existing Active.** Parsed model has `.Active(true)` (or `Active=true`) for an
  object whose snapshot `Active=false` → one `PatchFlagArgument(anchor, Active, "false")`; nothing else.
- **Reconcile — introduce Active when absent.** Statement has no `.Active(...)` call and snapshot
  `Active=false` → one `IntroduceFlagCall(anchor, Active, "false")` appended to the chain; no other edit.
- **Reconcile — Tag.** Snapshot `Tag="Enemy"` vs model `"Untagged"`/`"Player"` → `IntroduceFlagCall`
  (absent) or `PatchFlagArgument` (present) with the `"Enemy"` string literal.
- **Reconcile — Layer.** Snapshot `Layer=6` vs model `0`/other → `IntroduceFlagCall`/`PatchFlagArgument`
  with the integer literal `6`.
- **Reconcile — Static.** Snapshot `IsStatic=true`, statement has no `.Static()` → `IntroduceFlagCall(
  anchor, Static, null)` (no-arg `.Static()`). Snapshot `IsStatic=false` with a present `.Static()` →
  `RemoveFlagCall(anchor, Static)`.
- **Default-value / idempotence.** Snapshot flag equals the type default AND the call is absent → NO
  edit. After applying any flag patch, a re-parse of the patched source models the new value, so a
  second Reconcile against the same snapshot yields ZERO flag edits (no-op).
- **Redundant-default cleanup.** Model `.Active(false)` but snapshot `Active=true` (default) → the edit
  restores agreement by rewriting to the default (`PatchFlagArgument(anchor, Active, "true")`) or
  removing the call (`RemoveFlagCall`); either way a second Sync is a no-op. (Applier picks one form;
  pinned by the idempotence test.)
- **Multiple flags on one object.** Two flags changed on the same object → two independent edits on the
  same anchor, applied without disturbing each other or the transform/name args (M2) on that statement.
- **Composition with M2/M2b.** A single snapshot that moves an object (M2), creates another (M2b), AND
  retags a third (M2c) yields all three edit kinds in one `ReconcileResult`; each applies span-locally.
- **Fail loud, located (§7).** A flag change on an object whose builder statement can't be located
  (no source anchor) surfaces a `Conflict` (named + located), never a malformed edit.

### SourcePatchApplier behaviors
- **`PatchFlagArgument`** replaces only the argument token of the named flag call; surrounding trivia,
  the selector/other args, and unrelated statements are byte-for-byte unchanged.
- **`IntroduceFlagCall`** inserts the flag call at the END of the anchored statement's fluent chain,
  immediately before the terminating `;`, preserving the statement's existing formatting. String/int
  literals are emitted via the shared `SourceExpr` helper (hoisted in M2b under
  `SceneBuilder.Core/Reconcile/SourceExpr.cs`) so both directions format identically (REUSE it — string
  literals quoted/escaped, ints bare). `.Static()` is emitted with empty parens.
- **`RemoveFlagCall`** deletes the flag call (and its leading `.`) from the chain, leaving the rest of
  the statement and file formatting intact.

## Editor adapter deliverables
> **Built by the pipeline, gated by `./verify.sh`** (§8) — its Core layer plus, because this touches the
> Unity adapter, its mandatory Unity EditMode layer (the `unity-gate/` editor suite run against a live
> scene). The snapshot reader ALREADY provides the four flags (M2's full-scene reader reads
> `gameObject.tag`, `.layer`, `.activeSelf`, and `GameObjectUtility.GetStaticEditorFlags != 0` into
> `SnapshotNode.Tag/Layer/Active/IsStatic`), so M2c adds **no new adapter read**. The only adapter surface
> is that `SceneBuilderSync` already applies the returned `SourcePatch` — the new flag edit kinds flow
> through the existing apply path with no bespoke wiring. Runtime behavior is confirmed by the checklist.

- No new adapter code beyond confirming the existing snapshot reader populates the four flags for every
  scene object (it does, per M2). Reconcile stays in Core; the adapter passes the fresh snapshot and
  applies the patch (§2 logic-light).

## Authoring API added
None new. M2c consumes the M1 flag surface (`.Tag(s)`, `.Layer(i)`, `.Active(bool)`, `.Static()`) and
writes back into it — patching the argument of an existing call or introducing the call on the object's
statement.

## IdentityMap / sidecar changes
None. M2c edits only EXISTING mapped GameObjects; no entries are added, removed, or re-keyed (identity is
`GlobalObjectId`, unchanged by a flag edit). No `ReconcileResult` map delta.

## Core test plan (RED tests — behaviors, headless — §8)
`SceneBuilder.Core.Tests` (xUnit, `dotnet test`), behaviors not impl:
- `Reconcile_ActiveChanged_PatchesExistingActiveArgument` (`.Active(true)`→`.Active(false)`)
- `Reconcile_ActiveDeactivated_IntroducesActiveFalse_WhenCallAbsent` (append `.Active(false)`)
- `Reconcile_TagChanged_PatchesOrIntroducesTagCall` (`.Tag("Enemy")`)
- `Reconcile_LayerChanged_PatchesOrIntroducesLayerCall` (`.Layer(6)`)
- `Reconcile_StaticEnabled_IntroducesStaticCall_NoArg`
- `Reconcile_StaticDisabled_RemovesStaticCall`
- `Reconcile_DefaultValueWithAbsentCall_EmitsNoEdit` (Active=true / IsStatic=false / Tag="Untagged" / Layer=0 → no-op)
- `Reconcile_FlagSyncback_IsIdempotent_SecondSyncIsNoOp` (apply → re-parse → re-reconcile = zero flag edits)
- `Reconcile_MultipleFlagsOneObject_EmitsIndependentSpanLocalEdits`
- `Reconcile_FlagEdit_ComposesWith_MoveAndCreate_InOneReconcile` (M2 + M2b + M2c in one result)
- `Apply_IntroduceFlagCall_AppendsToChainBeforeSemicolon_FormattingPreserved` (f-suffix/quote via `SourceExpr`)
- `Apply_PatchFlagArgument_RewritesOnlyArgumentToken`
- `Apply_RemoveFlagCall_DeletesCall_PreservingSurroundingFormatting`
- `Reconcile_FlagEditWithNoSourceAnchor_SurfacesLocatedConflict`

## Unity confirmation checklist
1. Build a scene from a builder file (M1) so objects have `GlobalObjectId`s in the sidecar and at least
   one object (e.g. `Ground`) has **no** `.Active()` call in source.
2. In Unity, **uncheck Active** on `Ground` → Sync.
   *Expected:* `.Active(false)` is appended onto `Ground`'s statement; nothing else in the file changes;
   a **second Sync is a no-op**.
3. **Re-check Active** on `Ground` → Sync.
   *Expected:* the redundant `.Active(false)` is removed (or rewritten to `.Active(true)`); second Sync
   no-op.
4. **Retag** an object (e.g. set Tag = `Enemy`) → Sync.
   *Expected:* `.Tag("Enemy")` appears (introduced) or its existing argument updates; formatting intact.
5. **Change Layer** (e.g. to `6`) → Sync → `.Layer(6)` appears/updates.
6. **Toggle Static ON** for an object → Sync → `.Static()` is appended. **Toggle it OFF** → Sync → the
   `.Static()` call is removed. Each second Sync is a no-op.
7. Change **two flags on one object** (Active + Tag) in one edit → Sync → both calls update on that one
   statement, and its `.Transform(...)`/name args (M2) are untouched.

A milestone-DONE requires: Core tests green in CI, the adapter compile-check builds, **and** every
checklist step passing on a real edit (§8).

## Dependencies
- **M0** — Core/Editor/tests scaffold, sidecar, Plan round-trip harness.
- **M1** — `SceneModel` flag fields + parser lowering of `.Tag/.Layer/.Active/.Static`.
- **M2** — full `SceneSnapshot` reader (already reads the four flags), `Reconcile`, `Anchors`, and
  span-local `SourcePatch` apply — M2c reuses all of it.
- **M2b** — the shared `SourceExpr` literal helpers (reuse for chain-call emission) and the
  co-in-one-Reconcile composition pattern; M2c handles the mapped-edit case M2b's create/delete leaves
  open.

## Risks / notes
- **Static has no argument** — its truth value IS presence/absence of `.Static()`. Introduce for true,
  Remove for false; there is no `PatchFlagArgument` for Static. Pinned by the enable/disable tests.
- **Default-value discipline is load-bearing for idempotence.** Never introduce a default-valued call;
  restore agreement on a revert by patching-to-default or removing the call. The idempotence test guards
  against a flag edit that re-fires every Sync.
- **Boundary with M2b:** unmapped (scene-created) objects are M2b's; M2c must skip them so flags are not
  emitted twice (once in M2b's `AppendStatement`, once as a flag edit). Keyed on IdentityMap membership.
- **Chain-call insertion point:** append before the statement's terminating `;`, after existing chained
  calls (`.Transform(...)`, `.Id(...)`), so order stays deterministic and Roslyn trivia is preserved —
  string-splicing is forbidden (byte-diff test guards it).
- **Source is written directly via `WriteIfChanged`** (write only when the content differs) — no
  preview/confirm dialog; robust both-directions merge is M7.
