# needs_research — Concurrent multi-scene auto-sync (B)

**Status:** research stub, not a build milestone. This is the deferred **B** half of
`specs/29-multi-scene-builders.md`. Milestone 29 (A) ships **single-active-scene routing**: many
`ISceneDefinition` builders, one worked-in scene at a time, the builder governing the active/open scene
syncs. B is what remains: several builder-mapped scenes open **additively and dirty at once**, each
auto-syncing **concurrently**.

## Problem

When multiple builder-mapped scenes are open additively (`LoadSceneMode.Additive`) and dirty in the same
debounce window, auto-sync must do two things milestone 29 deliberately does NOT:

1. **Per-object owning-scene attribution (scene→code).** Each changed object must be attributed to its
   OWNING `GameObject.scene` — never `EditorSceneManager.GetActiveScene()` — then routed to that scene's
   builder and reconciled independently. A single active-scene read misattributes scene `B`'s edits to
   scene `A` whenever `A` is the active one. Milestone 29's `ExecuteSceneToCode` routes only the active
   scene (`SceneBuilderAutoSync.cs:410`, `GetActiveScene()`); B must group the changed `EntityId`s by owning
   scene and route each group.
2. **Per-scene suppression.** Suppressing our own write to scene `A` must not deafen a genuine concurrent
   user edit to scene `B`. Today `SuppressionScope.SceneWriteSuppressed` is a **global** flag
   (`SceneBuilderAutoSync.cs:256`, set around a write via `SceneBuilderBuild.SuppressScene()`,
   `SceneBuilderBuild.cs:198`) — correct only while one scene syncs at a time. B must key suppression by the
   Scene being written (or prove writes are synchronous-only so the pump never reads the flag for another
   scene's genuine edit).

Milestone 29's `SceneBuilderRouter` (discovery + `TryRouteScene`/`TryRouteBuilderFile`/`TryGetOpenScene`,
`com.codescenes/Editor/SceneBuilderRouter.cs`) already provides the builder↔scene routing table B needs;
B changes only WHO gets routed (per-owning-scene instead of the active scene) and the suppression keying.

## Current single-scene assumptions to lift (`SceneBuilderAutoSync.cs`)

- `EditorSceneManager.GetActiveScene()` reads in both executors (`:410`, `:502`) — the active-scene routing
  A relies on. B replaces the scene→code read with per-object `GameObject.scene` attribution.
- The **global** `SceneWriteSuppressed` flag (checked at `:256`, gates ALL scene→code accumulation while any
  scene write is in flight) — must become per-scene.
- The own-write content-hash registry for source files (`SuppressionScope.IsOwnWrite`, used at `:281-284`)
  is already keyed by file path, so it is per-builder-correct — verify, don't re-solve.
- The `sceneSaved` cold path (`:244-249`) already names its scene — B routes that named scene per-scene.

## Why deferred

- **Rare workflow.** Most users never have two builder-mapped scenes open, dirty, and editing concurrently;
  Unity multi-scene editing is an uncommon path. Milestone 29 (single active scene) covers the common need.
- **Real added complexity.** Per-object scene attribution and per-scene suppression touch the hottest part
  of the sync pump (the debounce/accumulate loop), which must stay effectively instant per keystroke
  (CLAUDE.md sync-performance constraint).

## Open questions to resolve before promotion

1. **Per-scene suppression keying.** Key `SceneWriteSuppressed` by the `Scene` being written, vs proving all
   scene writes are synchronous within one executor call so the pump only ever reads the flag for the same
   synchronous write. Which is provable/robust?
2. **Debounce/pump interaction across scenes.** One shared debounce timer vs per-scene batching; how a batch
   spanning edits to N scenes is split and reconciled without cross-write.
3. **`sceneSaved` cold path per named scene.** Routing the saved scene when several named scenes save close
   together.
4. **Performance of watching N builders concurrently.** The single `FileSystemWatcher` already covers all
   `*.cs` (`SceneBuilderAutoSync.cs:146-150`), but concurrent reconcile of N scenes per window needs a
   budget check.

## Acceptance criteria when promoted (the 2 checklist items deferred from milestone 29)

1. **Both scenes open, both edited in one window → each syncs its own.** With Alpha+Beta both open, edit an
   object in each, pump one scene→code cycle. **Expected:** each builder gets only its own scene's change;
   no cross-write. (`MultiScene_TwoOpenScenes_BothEdited_EachSyncsIndependently`)
2. **Suppression does not cross scenes.** While an in-flight suppressed write to `Alpha.unity` is simulated,
   a genuine user `ObjectChangeEvent` on `Beta.unity` must still accumulate and sync Beta.
   (`MultiScene_Suppression_DoesNotDeafenOtherScene`)

## Related

- **Builds on milestone 29** (`specs/29-multi-scene-builders.md`) — the `SceneBuilderRouter` + single-active
  routing shipped there is B's foundation.
- **Overlaps `specs/needs_research/multi-scene-additive.md`** — additive load is the main setup where B
  matters (manager scene + streamed chunks). That stub covers cross-scene `ValueNode.ObjectRef`,
  scene-qualified identity, and project-level vs per-scene `IdentityMap`; B here is specifically the
  concurrent auto-sync/attribution/suppression layer over additively-open scenes, not cross-scene refs.
