# M1b — Non-destructive Materialize (reconcile-into-existing Build)

Owns and TESTS the **§5 "Non-destructive invariant (Materialize)"**. Extends M1's `Diff`/`Materialize`/
`Plan` and M2's full scene-driven `SceneSnapshot` reader so that code→scene **Build** reconciles into
the EXISTING open scene in place — never `NewScene`/wipe-and-recreate — and never touches objects the
user hand-added in the editor. Binds to `00-foundation.md`. This is an EXTENSION of existing Core +
adapter — do not rebuild M0–M2; keep all existing tests green.

**Why this milestone exists (the gap it closes):** M1 shipped Build against an IdentityMap-only *minimal*
snapshot read, so `Diff` only ever saw objects it created — unmapped user objects were invisible by
accident. Once **M2's full, scene-discovering `SceneSnapshot` reader** feeds `Materialize` (it discovers
unmapped objects — §M2 In scope), §5's `absent-in-desired → DestroyObject` rule would, unguarded,
**DELETE** every GameObject/component the user hand-added in the editor, and any wipe-and-recreate Build
would churn `GlobalObjectId`s and break sync-back. No milestone owned the diff-level guard or the
in-place Build guarantee. M1b does.

### Additions to the contract
No new §3 value types. Reuses `Diff`/`ChangeSet`/`ChangeOp` (`RemoveNode`), `Materialize`/`Plan`/`PlanOp`
(`CreateObject`, `DestroyObject`, `RemoveComponent`, `SetField`, …), `SceneSnapshot`, `IdentityMap`,
`IdentityMapEntry`, `GlobalObjectId` exactly as typed in §3/§4/§5.
- **`IdentityMap.IsManaged(globalObjectId) → bool`** — the single, hoisted membership predicate: true iff
  some `IdentityMapEntry.GlobalObjectId` equals the argument (i.e. this tool created that actual from code
  on a prior build). This is the §5 guard's one source of truth; the Differ's removal path calls it so
  **every** code→scene caller of `Diff`/`Materialize` inherits the non-destructive invariant by default —
  it is NOT an opt-in flag any caller can forget (user's "fix the class, not the call site" rule).
- **Materialize's `actual` is now the full scene-driven `SceneSnapshot`** (M2's reader), replacing M1's
  IdentityMap-only minimal read for the Build path — so `Diff` sees unmapped user objects and must
  actively exclude them from removal rather than never seeing them.

## Goal
Running **Build** a second (or Nth) time reconciles the desired model into the already-open scene *in
place*: it creates only what's new, updates only what drifted, destroys only objects it previously
created that the code has since removed, and **leaves every user-hand-added object and component
untouched** — while every surviving coded object keeps its `GlobalObjectId` so sync-back (M2) still works
across rebuilds.

## In scope
- **In-place Build (no wipe).** The adapter reads the current scene into a full `SceneSnapshot` FIRST,
  then applies the `Plan`'s ops onto the currently-open scene. It NEVER calls `EditorSceneManager.NewScene`,
  never opens a fresh/empty scene, never clears the hierarchy before applying.
- **Diff against the REAL current snapshot, keyed on `GlobalObjectId`** (via IdentityMap ↔ LogicalId), for
  the three actual-vs-desired cases:
  - **CREATE** — a desired node with no matching mapped actual → `AddNode` → `CreateObject` (+ its
    transform/name/flag ops). Appended; existing siblings are NOT recreated.
  - **UPDATE** — a mapped actual that drifted from desired → in-place `SetName/Tag/Layer/Active/Static` /
    `SetField` (transform paths) ops on that SAME object; **never** a `RemoveNode`+`AddNode` pair (identity
    and `GlobalObjectId` preserved).
  - **DESTROY** — an actual whose `GlobalObjectId` **IS in the IdentityMap AND is absent from desired**
    (the code removed it) → `RemoveNode` → `DestroyObject`.
- **The §5 guard, enforced AT THE DIFF LEVEL.** For any actual-scene item whose `GlobalObjectId` is
  `!IdentityMap.IsManaged(...)` (unmapped ⇒ user-created in-editor), `Diff` emits **no** `RemoveNode` and
  **no** `RemoveComponent`, and never mutates it (no `SetField`/`SetName`/… targeting it). Unmapped actuals
  are simply invisible to Materialize's removal/mutation logic. This holds for whole GameObjects AND for
  individual user-added components on an otherwise-mapped object.
- **Identity preservation across rebuilds.** Surviving mapped objects keep their existing `GlobalObjectId`
  (no churn), so the sidecar stays valid and M2 sync-back continues to work after any number of Builds.
- **Idempotence.** When code == scene (nothing drifted, nothing added/removed in code), the produced `Plan`
  is empty (no-op Build).

## Out of scope
- Scene→code direction (`Reconcile`/`SourcePatch`) — that's M2/M2b; M1b only hardens the code→scene Build.
- Components/fields beyond M1's three transform paths as the *content* of create/update (M3 owns component
  materialization); M1b's component rule here is purely the **removal guard** — never `RemoveComponent` an
  unmapped (user-added) component. Adding/updating coded components is M3's concern and inherits this guard
  automatically once it emits `RemoveComponent`.
- Asset/cross-object refs, prefabs, RectTransform, UnityEvents (M4+).
- Merging concurrent both-directions edits, self-event suppression, domain-reload survival (M7).

## Core deliverables

### Types added/changed (referencing §3 contract)
- `IdentityMap` gains `IsManaged(globalObjectId) → bool` (flagged above) — the hoisted guard predicate.
- No changes to `SceneModel`/`SceneSnapshot`/`ChangeSet`/`Plan` shapes; `Diff`/`Materialize` behavior
  changes only. `Materialize` for Build now consumes M2's full `SceneSnapshot`.

### Functions/behaviors (each a testable contract)
- **Unmapped actual is never in the removal set.** Given a `SceneSnapshot` containing an object whose
  `GlobalObjectId` is absent from the IdentityMap and absent from desired, `Diff` emits NO `RemoveNode`
  for it (and `Materialize` emits no `DestroyObject`) — it is invisible to removal logic (§5).
- **Unmapped component is never removed.** Given a mapped object that in the snapshot carries an extra
  component whose identity is not in the map, `Diff`/`Materialize` emit NO `RemoveComponent` for it.
- **Mapped-and-code-removed actual IS destroyed.** Given an actual whose `GlobalObjectId` IS in the map but
  which has no matching node in desired, `Diff` emits `RemoveNode` → `Materialize` emits `DestroyObject`.
- **Drifted mapped actual is UPDATED in place.** Given a mapped actual whose position/name drifted from
  desired, `Diff` emits `SetField`/`SetName` on the existing object (matched by `GlobalObjectId`) and
  **never** a `RemoveNode`+`AddNode` pair; the object's `GlobalObjectId` is preserved.
- **New coded object appends without recreating siblings.** Given desired has one node more than the
  snapshot (all others mapped + equal), the `Plan` contains exactly one `CreateObject` (+ its ops) and
  **zero** ops touching the existing siblings.
- **Idempotent Build.** Given desired equals the full snapshot (all mapped, no drift), `Diff` yields an
  empty `ChangeSet` and `Materialize` an empty `Plan` (no-op).
- **Guard is inherited, not opt-in.** The membership gate lives inside `Diff`'s removal-emission path (via
  `IdentityMap.IsManaged`), so a caller that constructs a `Diff`/`Materialize` cannot bypass it; a test
  drives `Materialize` directly (no special flag) with an unmapped actual and asserts no destroy op.

## Editor adapter deliverables
> **Built by the pipeline, gated by the Unity-DLL compile-check** (`SceneBuilder.Editor.CompileCheck`, §8) —
> NOT hand-wired. The current Core-only pass excludes it (`dotnet test` can't run Unity APIs); a follow-on
> adapter pass builds it end-to-end, gated by `dotnet build SceneBuilder.sln` compiling the adapter against
> the real Unity 6000.5 DLLs. Only runtime behavior is confirmed by the user's checklist below.

- **`PlanExecutor` applies the `Plan` onto the currently-open scene, in place** (§5 step 5): it does NOT
  call `EditorSceneManager.NewScene` or otherwise wipe/replace the scene before applying. It resolves
  existing targets through `GlobalObjectId → object` (§2 responsibility #4) and mutates them; only
  `CreateObject` instantiates new GameObjects.
- **Read-snapshot-first Build flow.** The Build command reads the full current scene into a `SceneSnapshot`
  (M2's reader) BEFORE calling `Materialize`, passes that real snapshot as `actual`, then executes the
  returned `Plan` in place. There is no code path that constructs the scene from scratch.
- On `EditorSceneManager.sceneSaved`, record each newly `CreateObject`-ed object's real `GlobalObjectId`
  into its `IdentityMapEntry` and persist the sidecar (as M1); surviving objects' entries are left
  unchanged (no re-key, no churn).

## Authoring API added
None. M1b changes reconciliation semantics only; it consumes M1's `ISceneDefinition`/`SceneRoot` surface
unchanged.

## IdentityMap / sidecar changes
- `IdentityMap.IsManaged(globalObjectId)` predicate added (see Additions). No sidecar schema/shape change.
- Surviving objects keep their existing `GlobalObjectId` entries verbatim across rebuilds (no re-key);
  entries are added only for newly created objects (on save) and dropped only for mapped objects the code
  removed (whose `DestroyObject` executed).

## Core test plan
`SceneBuilder.Core.Tests` (xUnit, headless — §8). RED tests, behaviors not impl:
- `Diff_UnmappedActualAbsentFromDesired_IsNeverInDestroySet` (whole GameObject — the load-bearing §5 test).
- `Materialize_UnmappedUserComponent_IsNeverRemoved` (component-level guard).
- `Diff_MappedActualRemovedFromCode_ProducesDestroy`.
- `Diff_DriftedMappedActual_IsUpdatedInPlace_PreservingGlobalObjectId_NeverRemoveThenAdd`.
- `Materialize_CodeEqualsScene_ProducesEmptyPlan` (idempotent Build).
- `Materialize_AddedCodeObject_AppendsCreateOnly_WithoutRecreatingSiblings`.
- `Materialize_UnmappedActual_DirectCall_EmitsNoDestroy_GuardIsNotOptIn` (guard inherited via `IsManaged`).

## Unity confirmation checklist
1. Build `DemoScene` from its builder file (M1). *Expected:* scene materializes; sidecar has a
   `GlobalObjectId` per coded object.
2. In Unity, **hand-add a new GameObject** (e.g. `HandMade`) in the Hierarchy; do NOT add it to the code.
3. Edit the builder `.cs` (e.g. move a coded object, add one new coded object) and **Build again**.
   *Expected:*
   - `HandMade` **SURVIVES** untouched (not destroyed, not modified) — the §5 invariant holds on a real edit.
   - Coded objects update **in place**: same objects, **same `GlobalObjectId`s** (no duplicates, no
     wipe-and-recreate); the new coded object appears; a coded object removed from the file is destroyed.
4. Build once more with no source changes. *Expected:* no-op (no new/destroyed objects, no diffs applied).
5. After all rebuilds, **move a coded object in Unity and Sync** (M2). *Expected:* sync-back still patches
   the source — proving surviving objects kept their `GlobalObjectId`s across the rebuilds.

## Dependencies
- **M0** — Plan/JSON, IdentityMap sidecar, `PlanExecutor` + menu harness, layout.
- **M1** — parser, `Diff`/`ChangeSet`, `Materialize`/`Plan`, IdentityMap populated with `GlobalObjectId`s,
  transform/name/flag coverage.
- **M2** — the **full, scene-driven `SceneSnapshot` reader** that discovers unmapped objects; M1b's guard
  exists precisely to make that reader safe to feed into Build.

## Risks/notes
- **§5 is load-bearing and OWNED here:** the guard MUST live inside `Diff`'s removal path via
  `IdentityMap.IsManaged`, not in a Materialize caller — a call-site guard regresses the moment M3/M4/… add
  new `Remove*` emission and forget it. The direct-`Materialize` guard test pins that it is inherited.
- Component-level guard matters as soon as M3 emits `RemoveComponent`: an unmapped (user-added) component on
  a mapped object must never be removed. M1b establishes the predicate + test; M3's `RemoveComponent`
  emission inherits it by calling the same gate.
- Identity churn is the failure mode to avoid: any accidental `RemoveNode`+`AddNode` on a drifted object
  would mint a new `GlobalObjectId` and silently break M2 sync-back — the "updated in place, GlobalObjectId
  preserved" test guards this.
- **Sample seed (§12):** the hand-add-then-rebuild survival demo is part of the shipped `RoundTripDemo`
  script — it shows the moat (a Build that respects the user's own edits), not just construction.
