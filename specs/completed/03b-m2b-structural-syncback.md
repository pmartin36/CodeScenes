# M2b — Structural sync-back (create / delete objects, scene→code)

Extends M2 (which synced move/rename/reparent/reorder of EXISTING objects) to objects **created or
deleted in the editor**. Binds to `00-foundation.md` and reuses M2's `SourceEdit`/`SourcePatch`/
`Reconcile`/`Anchors`. This is an EXTENSION of existing Core — do not rebuild M0–M2; keep all existing
tests green.

### Additions to the contract
- **`SourceEdit` variants:** `AppendStatement { NewLogicalId, ParentAnchor?, Name, Transform?, Active?,
  Tag?, Layer?, IsStatic? }` (append a new `.Add(...)` builder statement for a scene-created object,
  under scene root when `ParentAnchor` is null, else under that parent); `RemoveStatement { Anchor }`
  (delete the statement for a scene-deleted object).
- **Handle introduction** (applied inside `AppendStatement` handling): appending a child under a coded
  parent that has no handle variable rewrites the parent statement `X.Add("P")…;` → `var <h> = X.Add("P")…;`
  (handle name derived from the parent name, uniquified against existing identifiers), then the child
  appends as `<h>.Add(…)`.
- **`ReconcileResult` map delta:** add `AddedEntries: IdentityMapEntry[]` and `RemovedLogicalIds: string[]`
  so the adapter updates the sidecar after applying the patch. (The created object's `GlobalObjectId` is
  already known from the snapshot — no re-save needed to capture it.)
- **Shared LogicalId synthesis:** the Reconciler predicts a created object's synthesized LogicalId using
  the SAME scheme `LogicalIdResolver` uses in the parser (hoist/share it) so the appended statement and
  the `AddedEntry` agree, and a second Sync is a no-op.

## Goal
Objects created or deleted in the Unity editor round-trip into the builder file: a new GameObject
appends a `.Add(...)` statement (introducing a parent handle if needed); a deleted GameObject removes
its statement. The sidecar updates to match, and anything not representable is surfaced as a `Conflict`
— never silently dropped.

## In scope
- **Create:** a snapshot object whose `GlobalObjectId` is NOT in the IdentityMap → `AppendStatement`
  carrying its `Name` + `Transform` (+ `Active`/`Tag`/`Layer`/`IsStatic` when non-default) under the
  right parent; mint a synthesized `NewLogicalId` + `AddedEntry {LogicalId↔GlobalObjectId}`.
- **New subtree:** parent + descendants all unmapped → append the parent with a handle and the
  descendants as `<handle>.Add(...)` (or nested), each with its own `AddedEntry`.
- **Handle introduction:** appending a child under a coded but handle-less parent introduces a `var`
  handle on the parent statement (uniquified), then references it.
- **Delete:** an IdentityMap GameObject entry whose `GlobalObjectId` is absent from the snapshot →
  `RemoveStatement` deleting that object's statement; `RemovedLogicalId` in the delta.
- **Reporting:** deleting an object whose handle is still referenced by a surviving (non-deleted)
  statement → `Conflict`, no removal. Any structural change that cannot be anchored → `Conflict`.
- Coexists with M2's move/rename/reparent/reorder edits in the same Reconcile.

## Out of scope
- **Components** on created objects are authored by M3 (M2b owns only the GameObject + transform + flags
  of the append). They are NOT dropped: a created object that carries components is appended AND its
  components attach onto that same just-appended statement in the SAME Reconcile pass (§13 rule 1),
  never deferred behind a conflict.
- Asset/cross-object references on created objects (M4/M5).
- Fine-grained sibling-order placement of appended statements beyond "root → end of Build body; child →
  after the parent statement (or its handle)".

## Core deliverables
- New `SourceEdit` records `AppendStatement`, `RemoveStatement`; `ReconcileResult.AddedEntries` +
  `RemovedLogicalIds`; hoisted/shared LogicalId synthesis usable by both parser and Reconciler.
- **Reconcile** behaviors (each a testable contract):
  - Unmapped snapshot root object → `AppendStatement` (ParentAnchor null) + `AddedEntry` with the
    predicted synthesized LogicalId + the snapshot `GlobalObjectId`; Name/Transform captured.
  - Unmapped snapshot child of a mapped parent → `AppendStatement` with that parent's anchor + `AddedEntry`.
  - Unmapped subtree (parent + child new) → append parent (handle) + child; both `AddedEntries`.
  - Mapped entry with `GlobalObjectId` absent from snapshot → `RemoveStatement(anchor)` + `RemovedLogicalId`.
  - Delete whose handle is referenced by a surviving statement → `Conflict`, no `RemoveStatement`.
  - A created object carrying components → append it AND attach its components onto that same
    just-appended statement in one pass (§13 rule 1); the components are never dropped and never
    deferred behind a conflict.
  - **Create-with-payload composition (§13).** M2b is the STRUCTURAL owner of the create-with-payload
    seam. When M2b appends a newly-created object, its payload (components/refs/events owned by
    M3/M4/M5/M8) is composed per §13 rule 1 — single-pass, dependency-ordered: the object statement is
    appended first, its `AddedEntry` maps it in-memory, then downstream stages attach payload onto that
    just-mapped statement — or, where single-pass is infeasible, converges on a guaranteed SECOND Sync of
    the unchanged scene per §13 rule 2 (append what it can, **report** every deferred piece with
    object + what + why, second Sync completes the remainder and is then a no-op). Silent partial
    drop is forbidden (§7). Cites §13 (create-with-payload).
  - **Delete cascade (§13 rule 4).** Deleting a created object emits `RemoveStatement` for its own
    statement AND removes the payload statements authored on it (components/refs/events); payload whose
    owning object survives is left untouched. Cites §13.
  - A structural change that cannot be anchored to a builder construct (e.g. a delete whose statement
    can't be located) → `Conflict` (fail-loud), never a malformed edit and never an unhandled throw.
- **SourcePatchApplier** behaviors:
  - `AppendStatement` inserts a well-formed `scene.Add("Name").Transform(pos: (…))` (floats **f-suffixed**
    via the shared `SourceExpr.Vec3Literal`/`SourceExpr.Float` helper — already hoisted out of
    `Reconciler.cs` into `SceneBuilder.Core/Reconcile/SourceExpr.cs`, so both directions format
    identically; REUSE it, do not reinvent) at end of the `Build` body for a root, or after the parent's statement for a
    child; introduces a `var <handle>` on the parent when absent (unique identifier); formatting preserved.
  - `RemoveStatement` deletes the target statement, preserving surrounding trivia/formatting.
- **Idempotence:** apply patch → re-parse → re-materialize/reconcile yields no further structural ops;
  with `AddedEntries` written to the map, a second Sync of an unchanged scene is a no-op.

## Editor adapter deliverables
> **Built by the pipeline, gated by `./verify.sh`** — its Core layer (`dotnet build` + `dotnet test`)
> plus, because this touches the Unity adapter, its mandatory Unity EditMode layer (the `unity-gate/`
> editor suite exercising the real behavior against a live scene). Adapter behavior is confirmed by an
> actual editor run, not a compile-check alone.

- `SceneBuilderSync`: after `Reconcile`, apply the `SourcePatch`, then update the sidecar — add
  `AddedEntries`, drop `RemovedLogicalIds` — and write it back. Log a one-line summary AND every
  `Conflict` (so nothing is silently dropped). No scene re-save needed (created objects' GlobalObjectIds
  come from the snapshot).

## Core test plan (RED tests — behaviors, headless)
- `Reconcile_NewRootObject_AppendsStatement_AndMapEntry`
- `Reconcile_NewChildOfMappedParent_AppendsUnderParent`
- `Reconcile_NewChildOfHandlelessParent_IntroducesHandle` (applier: parent gets `var`, child references it)
- `Reconcile_NewSubtree_AppendsParentHandleAndChild`
- `Reconcile_DeletedObject_RemovesStatement_AndDropsMapEntry`
- `Reconcile_DeleteWithReferencedHandle_SurfacesConflict_NoRemoval`
- `Reconcile_CreatedObjectWithComponents_AppendsAndReportsComponents`
- `Reconcile_CreatedObjectWithPayload_ConvergesNoSilentDrop` (§13 create-with-payload)
- `Reconcile_DeleteCascade_RemovesPayloadStatements` (§13 rule 4; surviving-owner payload untouched)
- `Append_AppliesWellFormedStatement_WithFSuffixedFloats_FormattingPreserved`
- `Remove_DeletesStatement_PreservingSurroundingFormatting`
- `Reconcile_UnanchorableStructuralChange_SurfacesConflict_DoesNotThrow`
- `StructuralSyncback_IsIdempotent_SecondSyncIsNoOp` (predicted LogicalId matches parser synthesis)

## Unity confirmation checklist
1. Build `DemoScene`. In Unity, add a new empty GameObject **under `Weapon`** → Sync → `DemoScene.cs`
   gains `var weapon = scene.Add("Weapon")…;` (handle introduced) + `weapon.Add("<name>")…;`, **compiles**,
   and a **second Sync is a no-op**.
2. Add a **root** GameObject → Sync → a new `scene.Add(…)` statement appears.
3. **Delete** an object in the scene → Sync → its statement is removed.
4. Add a component to a new object → Sync → the object appears WITH its `.Component<T>(…)` attached in
   the same Sync (§13 rule 1) — not dropped, not deferred.

## Dependencies
M2 (SourceEdit/SourcePatch/Reconcile/anchors + the float f-suffix emitter), M1 (parser + LogicalId
synthesis), M0.

## Risks/notes
- Handle introduction + identifier-uniquification is the meaty part; must not collide with existing names.
- The Reconciler's predicted synthesized LogicalId MUST match `LogicalIdResolver` exactly — share the code.
- Appended-statement placement affects sibling order; keep deterministic and idempotent.
