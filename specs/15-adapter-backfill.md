# Adapter Backfill — Unity Editor adapters for M1b / M2b / M2c / M3

**Status: NEXT TASK for a fresh session.** The Core of M0, M1, M2, M2b, M1b, M2c, M3 is built and test-green
(headless `dotnet test`). The Unity **Editor adapters** for the milestones after M2 are **not built** — the
plugin doesn't yet exercise the new Core capabilities in the editor. This spec backfills them.

## How to build these (read this first — it answers "why not the pipeline")
- **Do NOT use the tdd-pipeline.** It is test-driven; Unity adapter code has no headless runnable tests
  (it needs the editor). It doesn't fit the RED-test→code loop.
- **Spawn a subagent (general-purpose) to write the adapters — do NOT hand-wire them yourself.** One agent,
  working sequentially, because all four adapters touch the same few files
  (`com.scenebuilder/Editor/{PlanExecutor,SceneBuilderBuild,SceneBuilderSync,SceneSnapshotReader}.cs`) and
  parallel agents would collide.
- **Gate = the COMPILE-check:** `export PATH="$HOME/.dotnet:$PATH" && dotnet build SceneBuilder.sln`
  compiles `com.scenebuilder` Runtime+Editor against the installed Unity 6000.5 DLLs (via
  `SceneBuilder.Editor.CompileCheck`). Adapter is "done (compile)" only when `dotnet build SceneBuilder.sln`
  succeeds AND `dotnet test SceneBuilder.sln` still passes (Core untouched).
- **Runtime behavior is the USER's step** (the one manual thing): after compile-green, the user runs the
  Unity confirmation checklists in each milestone's spec. Only then does a milestone move to `specs/completed/`.

## The four adapters owed (each cites its milestone's Core API + spec "Editor adapter deliverables")

### 1. M1b — non-destructive, in-place Build (specs/02b-m1b-nondestructive-materialize.md)
Current `PlanExecutor.Execute(Plan)` builds into a **fresh scene** (creates new GameObjects only; comment
says "this first pass builds fresh"). Change `SceneBuilderBuild` + `PlanExecutor` to reconcile **in place**:
- `SceneBuilderBuild.BuildDemo`: read the CURRENT open scene via `SceneSnapshotReader.Read(activeScene)` +
  the sidecar `IdentityMap`; call `Materializer.Materialize(model, snapshot, map)` → `Plan`. Do **not**
  `EditorSceneManager.NewScene`.
- `PlanExecutor`: resolve each op's `LogicalId` to the EXISTING GameObject (map `LogicalId → GlobalObjectId`,
  then `GlobalObjectId → live object` — build that reverse index by walking the scene and calling
  `GlobalObjectId.GetGlobalObjectIdSlow(go).ToString()` per object, same as `SceneSnapshotReader`). Apply
  updates to existing objects; `CreateObject` only for new; `DestroyObject` only for mapped-and-removed.
  Never touch objects absent from the map (Core's `IdentityMap.IsManaged` already guarantees no
  `DestroyObject` op is emitted for them — the adapter just executes the Plan, so it inherits the guard).
- Re-capture `GlobalObjectId`s + rewrite sidecar after save (existing survivors keep theirs).

### 2. M2b — Sync applies structural edits + map deltas (specs/03b-m2b-structural-syncback.md)
`SceneBuilderSync.SyncDemo` already calls `Reconciler.Reconcile` + `SourcePatchApplier.Apply`. Extend:
- `SourcePatchApplier.Apply` already handles `AppendStatement`/`RemoveStatement` (Core). Ensure the adapter
  passes the parse `Anchors` and, for handle-introduction, any `reservedIdentifiers` the Reconcile overload
  wants (check `Reconciler.Reconcile` signature — it takes `reservedIdentifiers` + `flagPresence`).
- After applying the patch, **update the sidecar**: add `ReconcileResult.AddedEntries`, remove
  `RemovedLogicalIds`, write it back (no scene re-save — created objects' `GlobalObjectId`s come from the
  snapshot).
- Log every `Conflict` (referenced-handle, unrepresented-components, missing-anchor) — never silent.

### 3. M2c — Sync flags (specs/03c-m2c-flags-syncback.md)
`Reconciler.Reconcile(..., flagPresence)` needs per-object **FlagPresence** = which of `.Tag/.Layer/.Active/
.Static` physically appear on each statement. **VERIFY where this comes from:** check whether the M2c Core
exposes it on `ParseResult` (e.g. `ParseResult.FlagPresence`) — if so, the adapter just passes it; if not,
the adapter computes it from the parsed source. `SceneSnapshotReader` already reads Tag/Layer/Active/IsStatic
onto `SnapshotNode`. `SourcePatchApplier` applies `PatchFlagArgument`/`IntroduceFlagCall`/`RemoveFlagCall`
(Core). So the adapter's job is just: obtain FlagPresence, pass it to `Reconcile`, apply the resulting edits.

### 4. M3 — components + serialized fields (specs/04-m3-components-fields.md) — the big one
Build the Unity SerializedProperty layer:
- **`ResolveAuthoredPaths(SceneModel)`** — rewrite each transient `member:<name>` field key to its real
  serialized `propertyPath` using the component's `SerializedObject`, BEFORE any Diff (both directions).
- **Read**: iterate a component's `SerializedObject` visible properties (skip the bookkeeping set:
  `m_Script`, `m_ObjectHideFlags`, `m_CorrespondingSourceObject`, `m_PrefabInstance`, `m_PrefabAsset`,
  `m_GameObject`) → `ComponentData.Fields` keyed by serialized path; stamp the component's `GlobalObjectId`.
  Dispatch on `SerializedPropertyType` per M3's dispatch table (bool/int/long/float/double/string/enum
  [+[Flags]]/Vec2-3-4/Quat/Color/Nested/List; everything else → `Unsupported`).
- **Write**: apply `AddComponent`(`GameObject.AddComponent(Type)` resolved via `TypeCache`/full name) /
  `RemoveComponent`(`Object.DestroyImmediate`) / `SetField`(SerializedProperty write) / `ReorderComponent`
  (`ComponentUtility.MoveComponentUp/Down`). `ApplyModifiedProperties` once per component. Transform never
  added/removed/reordered.
- Feed the component-bearing snapshot into Reconcile/Materialize so M3's Core round-trips run for real.

## Then: M4 (not built here)
M4's Core (asset references) has NOT been built yet — it is the next milestone AFTER this adapter backfill
(run its Core pipeline per specs/05-m4-asset-references.md, then its adapter). Do M4 after the four adapters
above are compile-green.

## Definition of done (per milestone, foundation §8)
Core `dotnet test` green **AND** `dotnet build SceneBuilder.sln` green (adapter compiles) **AND** the user's
Unity confirmation checklist for that milestone passes on a real runtime edit. Only then move the milestone's
spec into `specs/completed/`.

## Handoff pointers
- Repo: /home/paul/Source/UnitySceneBuilder (branch build/m0-m2). Unity test project:
  /home/paul/Source/Unity/SceneBuilderTest (references the plugin via file: in Packages/manifest.json).
- dotnet at ~/.dotnet (PATH via ~/.zshenv). Unity 6000.5.3f1 DLLs at ~/Unity/Hub/Editor/6000.5.3f1/Editor/Data/Managed.
- Foundation contract: specs/00-foundation.md (§2 seam, §5 non-destructive invariant, §8 testing, §13 composition).
- Project memory: /home/paul/.claude/projects/-home-paul-Source-UnitySceneBuilder/memory/ (read MEMORY.md first).
