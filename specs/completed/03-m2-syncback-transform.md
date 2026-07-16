# M2 — Sync-back for transform / name / parent (THE MOAT PROOF)

### Additions to the contract
Named-but-untyped concepts from §2/§4/§5/§7 that M2 gives concrete shape; names reused verbatim later.
- **`SourcePatch` / `SourceEdit`** — the scene→code output (§2/§5 step 4). `SourcePatch` is an ordered
  `SourceEdit[]` over one builder file. `SourceEdit` variants (all span-local, formatting-preserving):
  `PatchArgument(anchor, argName, newExpr)` — used for both **move** (Transform args) and **rename**
  (name arg); `MoveStatement(anchor, newParentAnchor)` — **reparent**; `ReorderStatement(anchor,
  newSiblingIndex)` — **reorder**.
- **`SourceSpan`** — a Roslyn text span `{ Start:int; Length:int }` locating a construct.
- **`ParseResult`** — M1’s parser return type is extended to
  `ParseResult { SceneModel Model; IReadOnlyDictionary<string,SourceSpan> Anchors }`, where `Anchors`
  maps `LogicalId →` the invocation/statement span (§4 "Source anchor"). M1 consumers read `.Model`.
- **`Conflict`** — the §5/§7 "surfaced, never flattened" concept, typed:
  `Conflict { LogicalId:string?; GlobalObjectId:string?; Kind; Reason:string; Location:SourceSpan? }`.
- **`ReconcileResult { SourcePatch Patch; Conflict[] Conflicts }`** — Reconcile’s return.

No new §3 value types. `SceneSnapshot`, `SceneModel`, `ChangeSet` are used as already typed.

## Goal
Close the loop: when a user moves, renames, reparents, or reorders a GameObject in Unity, a full
scene snapshot is diffed against the expected model on `GlobalObjectId`, and the builder `.cs` source is
patched — formatting-preserving — so the code again matches the scene. This is the first proof of the
moat: a durable, living code↔scene relationship.

## In scope
- The **full, scene-driven `SceneSnapshot` reader** (adapter): reads the whole live scene, stamps every
  object with its `GlobalObjectId`, and — critically — **discovers objects the IdentityMap does not
  know** (the signal that the user edited the scene). Replaces M1’s IdentityMap-only minimal read.
- **`Reconcile(expected:SceneModel, actual:SceneSnapshot, IdentityMap) → ReconcileResult`** (§5): 
  `Diff` keyed on `GlobalObjectId` → `ChangeSet` → lower to `SourcePatch`, surfacing `Conflict`s.
- **Roslyn `SourcePatch` application** for the four edit kinds — move / rename / reparent / reorder —
  applied to the builder file preserving surrounding formatting/trivia (§5 step 4).
- **The fresh full snapshot is always the diff authority** (§5 correctness rule): Sync is invoked from
  the `SceneBuilder/Sync DemoScene (scene -> code)` menu and reconciles a fresh full snapshot diffed on
  `GlobalObjectId` — whatever triggers a Sync only says *when* to run, never bounds *what* is diffed.
  (Automatic event-driven triggering — `ObjectChangeEvents`/`sceneSaved`/domain-reload/focus-regain — is
  M-Auto; today Sync is menu-driven.)
- **Conflict surfacing** when an edit cannot be localized to a single builder construct (§5/§7).

## Out of scope
- Components, serialized fields, asset/cross-object refs, prefabs (M3+) — sync-back is limited to
  transform, name, parent, and sibling order.
- Deletion/creation *round-trip* into source beyond what rename disambiguation requires (append/remove
  statements broadly is M3+; M2 handles the four named edit kinds).
- Merging simultaneous edits in both directions (M7 robustness).

## Core deliverables

### Types added/changed (referencing §3 contract)
- `SceneSnapshot` (§3) now fully populated for the M2 surface (identity + `GlobalObjectId`, name,
  parent, sibling order, transform) across ALL scene objects including unmapped ones.
- New: `SourcePatch`, `SourceEdit` (4 variants), `SourceSpan`, `Conflict`, `ReconcileResult` (flagged).
- `ParseResult` extension (flagged): parser now also returns `Anchors` (`LogicalId→SourceSpan`).

### Functions/behaviors (each a testable contract)
- **Snapshot → diff → move patch.** Given expected model `Root.pos=(0,0,0)` and a snapshot where the
  same `GlobalObjectId` has `pos=(1,2,3)`, `Reconcile` produces a `PatchArgument` on `Root`’s
  `.Transform(pos:…)` setting `(1,2,3)`, and nothing else.
- **Snapshot → diff → rename patch.** Given the object with `Root`’s `GlobalObjectId` now named
  `"Base"`, `Reconcile` produces a `PatchArgument` rewriting the `Add("Root")` name argument to
  `"Base"` (LogicalId unchanged — identity is `GlobalObjectId`, not `Name`).
- **Snapshot → diff → reparent patch.** Given `Weapon` moved under a different parent in the snapshot,
  `Reconcile` produces a `MoveStatement` relocating `Weapon`’s builder statement under the new parent’s
  construct.
- **Snapshot → diff → reorder patch.** Given two siblings swapped in the snapshot, `Reconcile` produces
  a `ReorderStatement` (or the minimal pair of statement reorders) matching the new order.
- **Formatting preservation.** Applying any `SourceEdit` changes only the targeted span; indentation,
  comments, blank lines, and unrelated statements in the file are byte-for-byte unchanged.
- **Rename ≠ delete+create (disambiguation via GlobalObjectId).** Given a snapshot where an object’s
  `Name` changed but its `GlobalObjectId` matches an existing map entry, `Reconcile` emits a **rename**
  patch — NOT a `RemoveNode`+`AddNode`. Conversely, a NEW `GlobalObjectId` with a familiar name is
  treated as a new object, and a MISSING `GlobalObjectId` as a deletion (§4 correctness anchor).
- **The trigger never bounds the diff (§5).** However narrow the change that prompted a Sync — even a
  hypothetical `ObjectChangeEvents` batch naming only object A — `Reconcile` runs on a FRESH full
  snapshot, so if that snapshot shows A moved AND B renamed it patches BOTH.
- **Conflict when unlocalizable.** Given two same-named siblings with synthesized LogicalIds reordered
  such that the positional anchor is ambiguous (§4), `Reconcile` yields a `Conflict` (naming both
  candidates + location) instead of guessing a patch; no `SourceEdit` is emitted for that node.
- **Conflict when the target construct is absent.** An edit to an object whose builder statement can’t
  be found (e.g., created purely in Unity, no source anchor) surfaces a `Conflict` rather than a
  malformed patch (fail loud, located — §7).
- **Euler emission on move.** A move patch emits rotation as **Euler degrees** in source (§3 note) while
  the diff compared canonical quaternions.

## Editor adapter deliverables
- Full `SceneSnapshot` reader: walk the active scene, for each object read name/parent/order/transform
  and stamp `GlobalObjectId` (§2 responsibility #2); include objects absent from the IdentityMap.
- Run Reconcile from the `SceneBuilder/Sync DemoScene (scene -> code)` menu command: read the fresh full
  snapshot and hand it to Core (§2 logic-light — the adapter never computes diffs itself). (Automatic
  triggering — `ObjectChangeEvents`/`sceneSaved`/domain-reload/focus-regain — is M-Auto.)
- Write the returned `SourcePatch` straight to the builder file via `WriteIfChanged` (write only when the
  content differs; no preview/confirm dialog), log any `Conflict`s, and refresh the IdentityMap (any
  newly stamped `GlobalObjectId`s for objects now anchored to source).
- `GlobalObjectId ↔ object` resolution for both directions (§2 responsibility #4).

## Authoring API added
None new. M2 consumes the M1 authoring surface and writes back into it. It formalizes the round-trip
constraint (§6): only files whose `Build` body is composed of recognized builder calls are patchable;
non-conforming files fail loud/located and are not patched.

## IdentityMap / sidecar changes
- On sync-back, entries are re-keyed/validated by `GlobalObjectId` (the correctness anchor — §4); a
  rename updates the object’s `Name`-derived expectations but NOT its `LogicalId`.
- Objects discovered in the scene but missing from the map get entries once anchored to a source
  statement; objects whose `GlobalObjectId` vanished are marked deleted (patch removes their statement
  only within the four-edit-kind scope; broader delete round-trip is M3+).
- No `Assets`/`Component` changes (M3+/M4+).

## Core test plan
`SceneBuilder.Core.Tests` (xUnit, headless — §8). RED tests, behaviors not impl:
- `Reconcile_Move_ProducesTransformArgumentPatch`.
- `Reconcile_Rename_ProducesNameArgumentPatch_LogicalIdUnchanged`.
- `Reconcile_Reparent_ProducesMoveStatement`.
- `Reconcile_Reorder_ProducesReorderStatement`.
- `SourcePatch_Apply_PreservesUnrelatedFormattingAndComments` (byte-diff outside the span).
- `Reconcile_RenamedSameGlobalObjectId_IsRename_NotDeleteThenCreate`.
- `Reconcile_NewGlobalObjectId_IsCreate_MissingGlobalObjectId_IsDelete`.
- `Reconcile_EventBatchNarrowerThanSnapshot_PatchesAllSnapshotEdits` (snapshot is authority).
- `Reconcile_AmbiguousSynthesizedSiblings_SurfacesConflict_NoPatchForNode`.
- `Reconcile_EditWithNoSourceAnchor_SurfacesLocatedConflict`.
- `Reconcile_MovePatch_EmitsEulerRotation_WhileDiffingQuaternion`.
- `Parse_ReturnsAnchors_MappingLogicalIdToInvocationSpan`.

## Unity confirmation checklist
1. Build a scene from a `FooScene` builder file (M1), so objects have `GlobalObjectId`s in the sidecar.
2. In Unity, **move** `Root` (change its position) → run Sync (the `SceneBuilder/Sync …` menu).
   *Expected:* `FooScene.cs` `Root.Transform(pos:…)` argument updates to the new position; nothing else
   in the file changes.
3. **Rename** a child in the Hierarchy → Reconcile.
   *Expected:* the corresponding `Add("…")` name argument updates; the object’s `LogicalId` (and its map
   entry) is unchanged — proving rename ≠ delete+create.
4. **Reparent** a child (drag under a different parent) → Reconcile.
   *Expected:* that object’s builder statement moves under the new parent’s construct, formatting intact.
5. **Reorder** two siblings → Reconcile.
   *Expected:* the statements reorder to match the new sibling order; `GlobalObjectId`s preserved.
6. Create two same-named siblings and reorder to force ambiguity → Reconcile.
   *Expected:* a **conflict** is surfaced (naming candidates + location), NOT a silent/incorrect patch.
7. Make several edits at once (e.g. move one object AND rename another), then run Sync ONCE.
   *Expected:* the full-snapshot diff catches BOTH edits and patches source — a single Sync is never
   bounded to one change.

## Dependencies
- **M0** — harness, Plan, sidecar, layout.
- **M1** — builder parser (`SceneModel` + now `Anchors`), canonical serializer, `Diff`/`ChangeSet`,
  IdentityMap populated with real `GlobalObjectId`s, transform/name/parent/order model coverage.

## Risks/notes
- **Correctness rule (§5) is load-bearing:** the diff authority is always a fresh full `SceneSnapshot`
  on `GlobalObjectId`; whatever triggers a Sync only decides *when* to run, never *what* is diffed. Tests
  pin that a narrower change never bounds the patch set. (When M-Auto adds automatic triggers —
  `ObjectChangeEvents`, domain reload, focus-regain — this same full-snapshot rule keeps them safe.)
- Rename/move disambiguation depends entirely on `GlobalObjectId` stability (§4); if an object has no
  `GlobalObjectId` yet (never saved), it can only be treated as new — surfaced, not guessed.
- Formatting preservation requires editing via Roslyn syntax-node replacement with original trivia, not
  string splicing; the byte-diff test guards regressions.
- Self-triggered writes (our own patch causing a re-Sync echo) are only *noted* here; robust
  self-event suppression is M7. M2 avoids loops by reconciling against the freshly written source’s
  model (which now matches the scene → empty diff).
- Source is written directly via `WriteIfChanged` (write only when the content differs) — no
  preview/confirm dialog; robust both-directions merge / self-event suppression is M7.
