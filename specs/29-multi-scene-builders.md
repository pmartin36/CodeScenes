# M-MultiScene — Multiple scene builders, each auto-synced to its own scene

> **SCOPE — single active scene (A).** This milestone is **single-active-scene routing**: the project has
> many `ISceneDefinition` builders, you work in ONE scene at a time, and the builder governing the
> **active/open** scene syncs (both directions). Concurrent auto-sync of multiple simultaneously-open **and
> dirty** scenes — additive multi-scene editing where each open scene syncs concurrently, requiring
> per-object owning-scene attribution and per-scene suppression — is **DEFERRED** to
> `specs/needs_research/concurrent-multi-scene-sync.md` (B). B is a rare workflow; A covers the common need.
> Wherever routing must pick a scene, this spec resolves it as the **active/open** scene, never as one of
> several concurrently-dirty scenes.

> **Why this milestone exists.** CodeScenes' live auto-sync governs exactly ONE hardcoded builder today.
> `com.codescenes/Editor/SceneBuilderAutoSync.cs:45` pins `private const string BuilderName = "DemoScene";`,
> and every routing decision in the driver resolves the governing builder as
> `SceneBuilderBuild.LastBuilderPath ?? SceneBuilderPaths.Builder(BuilderName)`
> (`SceneBuilderAutoSync.cs:421-422`, `:509-510`, `:562`, `:615-616`) — the last builder this session
> *built*, falling back to the `DemoScene` constant. Both direction executors then hard-assume the
> **active** scene: `ExecuteSceneToCode` and `ExecuteCodeToScene` both open with
> `EditorSceneManager.GetActiveScene()` (`SceneBuilderAutoSync.cs:410`, `:502`). The net effect: a
> code→scene file-watcher event for any builder that is not the last-built/`DemoScene` one is **silently
> dropped** — `ExecuteCodeToScene` returns early because the changed path does not equal the single
> `fullBuilderPath` it computed (`SceneBuilderAutoSync.cs:514-528`). This was surfaced by live testing: an
> isolated `LiveVerify` builder is ignored because it is not "DemoScene". A real product has many scene
> builders — each an `ISceneDefinition` (`com.codescenes/Runtime/ISceneDefinition.cs:7`) mapped to its own
> scene — and every one of them must auto-sync, in both directions, independently and instantly.
>
> **This is an ADAPTER milestone.** The Core round-trip (parse → reconcile → patch) is already
> per-builder-parameterized: `SceneBuilderBuild.Run(builderPath, scenePath, sidecarPath, scene)`
> (`SceneBuilderBuild.cs:132`) and `SceneBuilderSync.Run(builderPath, sidecarPath, scene, …)`
> (`SceneBuilderSync.cs:121`) take every path + scene explicitly and share no builder-global state. The gap
> is entirely in the **routing layer** in `com.codescenes/Editor/` — which builder a watcher event belongs
> to, and which scene an `ObjectChangeEvent` belongs to. Per CLAUDE.md, adapter work is **not complete
> without EditMode coverage** exercising real multi-builder routing against live scenes.

---

## Additions to the contract

No new Core POCOs. This milestone introduces one adapter concept — a **builder↔scene routing table** —
and removes the single-builder assumption. Flagged here per foundation §1.

```
BuilderRoute                          // adapter-only record (com.codescenes/Editor), one per discovered
                                      //   ISceneDefinition. NOT a Core type; never serialized.
  BuilderName   : string             // the ISceneDefinition type's Name — the file-stem convention
                                      //   SceneBuilderPaths.Builder/Sidecar already use (name + ".cs" /
                                      //   ".sbmap.json"), see SceneBuilderPaths.cs:35,38
  BuilderPath   : string             // absolute — SceneBuilderPaths.Builder(BuilderName)
  SidecarPath   : string             // absolute — SceneBuilderPaths.Sidecar(BuilderName)
  ScenePath     : string             // project-relative "Assets/.../X.unity". Resolved from the sidecar's
                                      //   IdentityMap.Scene field (IdentityMap.cs:12, persisted by
                                      //   SceneBuilderBuild.Run at SceneBuilderBuild.cs:213 "Scene = scenePath")
                                      //   when the sidecar exists; else the deterministic default
                                      //   "Assets/SceneBuilder/<BuilderName>.unity" (mirrors the current
                                      //   SceneBuilderBuild.ScenePath const, SceneBuilderBuild.cs:33).
```

**`SceneBuilderRouter`** — a new static adapter class (`com.codescenes/Editor/SceneBuilderRouter.cs`) that
owns discovery + the two lookup directions. It replaces the three copies of the `BuilderName` constant
(`SceneBuilderAutoSync.cs:45`, `SceneBuilderBuild.cs:28`, `SceneBuilderSync.cs:26` — the "third copy;
flagged, not refactored here" the code itself calls out at `SceneBuilderAutoSync.cs:43-44`) with a single
discovery source of truth. `LastBuilderPath`/`LastSidecarPath` (`SceneBuilderBuild.cs:46-47`) are retired
as the *routing* mechanism (they remain valid as a build-result echo but are no longer consulted for
"which builder governs a change").

**Ledger:** the `SceneBuilderRouter` (discovery + both lookup directions), the active-scene routing of both
executors, the per-builder menu generalization, and the removal of the `BuilderName` constant are all
owned by **M-MultiScene**. No Core file changes shape.

---

## Goal

Every `ISceneDefinition` in the project is a first-class scene builder that auto-syncs to its own scene,
both directions. You work in ONE scene at a time: the builder governing the active/open scene syncs, and
switching to a different builder's scene switches which builder governs. Editing builder file `A.cs` (with
`A.unity` open) builds scene `A.unity` and touches nothing else; editing the active scene `B.unity`
reconciles builder `B.cs` and touches nothing else. No hardcoded `"DemoScene"`, no "last built wins", no
dropped events for a builder that is not the active one. (Concurrent sync of several simultaneously-dirty
scenes is B — deferred; see scope banner.) The single master toggle
(`SceneBuilderAutoToggle`, `SceneBuilderAutoToggle.cs`) still governs both directions for all builders at
once — auto is ON by default, no buttons on the happy path.

## In scope

- **Builder discovery** via `TypeCache.GetTypesDerivedFrom<ISceneDefinition>()` (the same TypeCache
  mechanism the adapter already uses for component types, e.g. `ComponentTypeResolver.cs`), producing one
  `BuilderRoute` per concrete implementation. Each route's builder/sidecar paths come from
  `SceneBuilderPaths.Builder(name)`/`Sidecar(name)` (`SceneBuilderPaths.cs:35,38`); each route's scene
  comes from the sidecar's `IdentityMap.Scene` field, or the deterministic default when no sidecar exists.
- **The governing builder is the one whose scene is the active/open scene.** For a sync cycle the governing
  builder is the discovered builder whose mapped scene is the currently **active/open** scene. A builder
  whose scene is not open is skipped for scene→code (there is no live scene to read); for code→scene, a
  changed builder file whose scene is not the open scene is skipped (a located log — never a build into the
  wrong active scene), see Tradeoffs.
- **Code→scene routing per builder.** A watcher event names a changed builder FILE; map file → `BuilderRoute`
  → its OWN scene; build THAT scene **if it is the open/active scene**. Non-`DemoScene` events are no longer
  dropped (removing the event-drop is the headline bug fix); the target is the routed builder's own scene
  resolved as the open scene, not `GetActiveScene()` with the last-built builder's paths.
- **Scene→code routing by the active scene.** Route the ACTIVE scene
  (`EditorSceneManager.GetActiveScene()`) to its governing `BuilderRoute` and reconcile THAT builder's
  source against that scene. No per-object owning-scene attribution across concurrently-dirty scenes (that
  is B). An edit in a scene with no governing builder is a no-op.
- **Per-builder sidecar/state isolation.** Each builder owns its own `*.sbmap.json`
  (`SceneBuilderPaths.Sidecar(name)`); one builder's identity map is never read or written while
  reconciling another's scene.
- **Suppression stays global (unchanged).** The existing global `SceneWriteSuppressed` self-write guard is
  correct when one scene syncs at a time, and is kept as-is. Per-scene suppression (so writing scene `A`
  does not deafen a concurrent edit to scene `B`) is a **B** concern — deferred, not addressed here.
- **Menu generalization.** `Build DemoScene`/`Sync DemoScene` (`SceneBuilderBuild.cs:79`,
  `SceneBuilderSync.cs:81`) become "act on the active scene's builder" plus a "build all"/"sync all"
  affordance — retained as debug scaffolding only (CLAUDE.md: the happy path has no buttons).

## Out of scope

- **Additive multi-scene semantics** — cross-scene object references, additive load ORDER expressed in
  code, and reconcile when several *additively-loaded* scenes are edited as one unit. That is the research
  stub `specs/needs_research/multi-scene-additive.md` (cross-scene `ValueNode.ObjectRef`, scene-qualified
  identity, project-level vs per-scene IdentityMap). **This milestone supports many builders each mapped to
  ONE scene, and routes the single active/open scene to its builder** — it does NOT introduce cross-scene
  references or a composed-multi-scene builder. A builder still owns exactly one scene; `IdentityMap` stays
  per-scene (`IdentityMap.cs:12`).
- **Concurrent auto-sync of several simultaneously-open+dirty scenes (B).** Per-object owning-scene
  attribution and per-scene suppression when multiple builder-mapped scenes are open additively and edited
  concurrently. Deferred to `specs/needs_research/concurrent-multi-scene-sync.md`; A routes only the
  active/open scene.
- **Auto-loading a builder's scene that is not open.** Scene→code cannot read a scene that is not loaded;
  code→scene against a closed scene is bounded to "load it single/additively if resolvable, else skip with
  a located log" (see Tradeoffs) — never a surprise scene-open on the user. No forced multi-scene open.
- **Changing the Core round-trip.** `SceneBuilderBuild.Run`/`SceneBuilderSync.Run` already take explicit
  paths+scene; this milestone only changes who calls them with which arguments.
- **Renaming builders / changing the file-stem convention.** The `<TypeName>.cs` ⇄ `<TypeName>.sbmap.json`
  convention (`SceneBuilderPaths.cs:35,38`) is reused verbatim as the discovery key.

## Core deliverables

**None — this milestone is adapter-only.** The Core seams it depends on already exist and are unchanged:
`SceneBuilderBuild.Run(builderPath, scenePath, sidecarPath, scene)` (`SceneBuilderBuild.cs:132`),
`SceneBuilderSync.Run(builderPath, sidecarPath, scene, preAssembledSnapshot)` (`SceneBuilderSync.cs:121`),
`SceneBuilderSync.RunConflictAware(...)` (`SceneBuilderSync.cs:372`), and the per-builder `IdentityMap`
(`IdentityMap.cs`). If a routing need surfaces a genuinely missing Core capability during build, it is a
scope change to be flagged — the expectation is zero Core edits.

## Editor adapter deliverables

### 1. `SceneBuilderRouter` (new: `com.codescenes/Editor/SceneBuilderRouter.cs`)

- **`IReadOnlyList<BuilderRoute> Discover()`** — enumerate
  `TypeCache.GetTypesDerivedFrom<SceneBuilder.Authoring.ISceneDefinition>()`, filter to concrete
  non-abstract classes, and build one `BuilderRoute` per type: `BuilderName = type.Name`,
  `BuilderPath = SceneBuilderPaths.Builder(type.Name)`, `SidecarPath = SceneBuilderPaths.Sidecar(type.Name)`,
  and `ScenePath` = the sidecar's `IdentityMap.Scene` when `File.Exists(SidecarPath)`
  (`IdentityMapJson.Deserialize`, as `SceneBuilderAutoSync.cs:428` already does), else
  `"Assets/SceneBuilder/" + type.Name + ".unity"`. Skip a type whose builder `.cs` does not exist on disk
  (a defined-but-unbuilt type has nothing to watch). Deterministic order (by `BuilderName`, Ordinal).
- **`bool TryRouteBuilderFile(string changedFullPath, out BuilderRoute route)`** — the **code→scene**
  lookup: normalize with `Path.GetFullPath` (matching `SceneBuilderAutoSync.cs:514`) and match against each
  route's `BuilderPath`. Replaces the single-`fullBuilderPath` equality gate at
  `SceneBuilderAutoSync.cs:514-524`.
- **`bool TryRouteScene(Scene scene, out BuilderRoute route)`** — the **scene→code** lookup: match
  `scene.path` (project-relative) against each route's `ScenePath`, Ordinal. A scene with no governing
  builder yields false (skip — see Tradeoffs).
- **`bool TryGetOpenScene(BuilderRoute route, out Scene scene)`** — enumerate open scenes
  (`EditorSceneManager.sceneCount` + `EditorSceneManager.GetSceneAt(i)`) and return the loaded one whose
  `path` equals `route.ScenePath`; false when the route's scene is not open.

### 2. Rewire `SceneBuilderAutoSync` routing (both executors)

- **Delete `const string BuilderName` (`:45`)** and every `LastBuilderPath ?? Builder(BuilderName)` /
  `LastSidecarPath ?? Sidecar(BuilderName)` fallback pair (`:421-422`, `:509-510`, `:562`, `:615-616`).
  Replace with `SceneBuilderRouter` lookups.
- **`ExecuteCodeToScene(paths)` (`:500-551`) — real routing table, not the current single-builder drop.**
  For each changed `path`, `TryRouteBuilderFile` → its `BuilderRoute`; if the route's OWN scene is the
  open/active scene (`TryGetOpenScene`),
  `SceneBuilderBuild.Run(route.BuilderPath, route.ScenePath, route.SidecarPath, openScene)` into THAT scene
  (not `GetActiveScene()` with the last-built builder's paths). If the routed builder's scene is not open,
  skip it with a located log (never build into the wrong active scene). Preserve the existing parse-error
  located-log + scene-untouched behavior (`:530-550`) per builder. Preserve `CaptureBaseline` at the
  converged tail (`:543`) **per builder** (see §3).
- **`ExecuteSceneToCode(ids)` (`:408-455`) — route the ACTIVE scene to its builder.** Resolve the active
  scene via `EditorSceneManager.GetActiveScene()` (`:410`, kept) and `TryRouteScene` → its `BuilderRoute`.
  Reconcile that builder against the active scene using its own sidecar
  (`SceneBuilderSync.Run(route.BuilderPath, route.SidecarPath, scene, snapshot)` — `:449`). The `sceneSaved`
  cold path (`:244-249`) already names its scene; route that named scene the same way. When the active scene
  has no governing builder, the cycle is a no-op. **Do NOT** attribute changed objects to their owning
  `GameObject.scene` across concurrently-dirty scenes — that per-object attribution is B (deferred).
- **`NeedsSaveForDurableId` (`:469-493`)** stays, but is evaluated against the ROUTED builder's sidecar
  map, per scene.

### 3. Per-builder snapshot assembler + baseline

- The session-local `_snapshotAssembler` (`:78`) and the `BaselineSnapshot`/`BaselineSource`
  (`:61-62`) are currently single-builder globals. Make them **keyed by builder** (a
  `Dictionary<string, ChangeScopedSnapshot>` and a `Dictionary<string, (string source, SceneSnapshot snap)>`
  keyed on `BuilderName`), so scene `A`'s incremental snapshot and conflict baseline never bleed into scene
  `B`'s reconcile. `CaptureBaseline` (`:560-586`) and `ExecuteBothChanged` (`:598-648`) become per-route.
  `ResetForTests` (`:663-690`) clears all per-builder state.

### 4. Suppression — global, unchanged

- Keep the existing global suppression as-is. `SceneWriteSuppressed` (checked at `:256`) gating ALL
  scene→code accumulation while a scene write is in flight is **correct when one scene syncs at a time**,
  which is exactly this milestone's scope. Do NOT make suppression per-scene here — that is a **B** concern
  (`specs/needs_research/concurrent-multi-scene-sync.md`), where writing scene `A` must not deafen a genuine
  concurrent user edit to scene `B`. The own-write content-hash registry for source files
  (`SuppressionScope.IsOwnWrite`, used at `SceneBuilderAutoSync.cs:281-284`) is already keyed by file path,
  so it is per-builder-correct — no change needed.

### 5. Menu generalization (debug scaffolding only)

- Replace `[MenuItem("CodeScenes/Build DemoScene …")]` (`SceneBuilderBuild.cs:79`) and
  `[MenuItem("CodeScenes/Sync DemoScene …")]` (`SceneBuilderSync.cs:81`) with:
  - **`CodeScenes/Build Active Scene (code → scene)`** — `TryRouteScene(GetActiveScene())` → build that
    builder; located error if the active scene has no governing builder.
  - **`CodeScenes/Sync Active Scene (scene → code)`** — symmetric.
  - **`CodeScenes/Build All`** / **`CodeScenes/Sync All`** — iterate `Discover()`, act on each route whose
    scene is open. These remain *debug tools*, not the product path (CLAUDE.md).
- Remove the now-dead `BuilderName`/`ScenePath`/`BuilderPath`/`SidecarPath` members in
  `SceneBuilderBuild` (`:28,33,35,36`) and `SceneBuilderSync` (`:26,31,32`), routing through
  `SceneBuilderRouter`/`GetActiveScene()` instead. The `Run` seams themselves are unchanged.

## Unity confirmation checklist (⇒ EditMode tests in `unity-gate/Assets/GateTests/`)

Each item is a real editor action; each maps 1:1 to an EditMode test that exercises **two** builders
(`RouteAlpha`, `RouteBeta`) mapped to **two** scenes (`Alpha.unity`, `Beta.unity`), against real
`GameObject`/`Scene`/`GlobalObjectId`/`AssetDatabase` (the boundary the headless Core tests are blind to,
CLAUDE.md). Follow `AutoCodeToSceneTests.cs` / `AutoSceneToCodeTests.cs` setup (seed via
`SceneBuilderBuild.Run`, drive via `SceneBuilderAutoSync.Execute*` directly). Suggested file:
`unity-gate/Assets/GateTests/MultiSceneRoutingTests.cs`.

1. **Discovery enumerates all builders, not one.** `SceneBuilderRouter.Discover()` returns a
   `BuilderRoute` for every project `ISceneDefinition` whose `.cs` exists — with correct
   builder/sidecar/scene paths (scene read from each sidecar's `IdentityMap.Scene`) — and does **not**
   collapse to `DemoScene`. (`MultiScene_Discover_EnumeratesAllBuilders`)
2. **Code→scene routes the RIGHT scene; the other is untouched (the bug).** With Alpha+Beta built and
   `Beta.unity` the open/active scene, an external edit to `RouteBeta.cs` adding object `Gamma`, then
   `ExecuteCodeToScene(new[]{ betaBuilderPath })`. **Expected:** `Beta.unity` gains `Gamma` (live + saved
   asset); `Alpha.unity` is byte-identical/unchanged. A `LiveVerify`-style builder that is NOT the
   last-built is **no longer dropped**. (`MultiScene_CodeToScene_RoutesToOwningScene_LeavesOtherUntouched`)
3. **Scene→code routes the RIGHT builder; the other source is untouched.** With `Alpha.unity` active, move
   a `GameObject` in it, `ExecuteSceneToCode` with that object's `EntityId`. **Expected:** `RouteAlpha.cs`
   is patched with the new transform; `RouteBeta.cs` is byte-identical.
   (`MultiScene_SceneToCode_RoutesToOwningBuilder_LeavesOtherUntouched`)
4. **Sidecar isolation.** After a full round-trip on both, assert `RouteAlpha.sbmap.json` contains only
   Alpha's LogicalIds/`Scene=Alpha.unity` and `RouteBeta.sbmap.json` only Beta's — one map never leaks the
   other's entries. (`MultiScene_Sidecars_DoNotCrossContaminate`)
5. **Builder whose scene is NOT open.** Close `Beta.unity` (leave Alpha open/active), fire a `RouteBeta.cs`
   watcher event. **Expected:** no exception, no write to Alpha, a located skip/log for Beta (not a silent
   build into the wrong active scene). (`MultiScene_CodeToScene_SceneNotOpen_SkipsSafely`)
6. **Scene active with no governing builder.** Make active a plain scene that no `ISceneDefinition` maps
   to; a scene edit there is a no-op — no write to any builder and no exception.
   (`MultiScene_SceneToCode_NoGoverningBuilder_NoOp`)
7. **Menu: active-scene routing.** With Beta active, `CodeScenes/Build Active Scene` builds `RouteBeta`
   into `Beta.unity`; with no governing builder active it logs a located error and writes nothing.
   (`MultiScene_Menu_BuildActiveScene_TargetsActiveBuildersScene`)

> **Deferred to B** (`specs/needs_research/concurrent-multi-scene-sync.md`), promoted as its acceptance
> criteria: *both scenes open and edited in one window → each syncs its own* (was #4) and *suppression does
> not cross scenes* (was #7). Both require concurrently-dirty-scene attribution / per-scene suppression,
> which is out of this milestone's single-active scope.

## Dependencies

- **M-Auto (`specs/completed/14-m-auto-live-sync.md`), SHIPPED. HARD dependency.** This milestone
  generalizes the driver M-Auto built: the debounce pump, `changesPublished`/`sceneSaved`/`FileSystemWatcher`
  transport, `SuppressionScope`, and the single master toggle (`SceneBuilderAutoToggle`) are all reused; only
  the single-builder routing choice is replaced. The `Execute*` executor seams and `ResetForTests` remain
  the test entry points.
- **`SceneBuilderPaths` (`SceneBuilderPaths.cs`)** — `Builder(name)`/`Sidecar(name)` (`:35,38`) are the
  discovery key; `EnsureBuildersDirectory` (`:85`) and the `WriteIfChanged` self-echo guard (`:132`) are
  unchanged.
- **The per-scene `IdentityMap` (`IdentityMap.cs`)** — its `Scene` field (`:12`), already written by
  `SceneBuilderBuild.Run` (`SceneBuilderBuild.cs:213`), is the authoritative builder→scene link that makes
  routing possible without a new persisted registry.
- **`TypeCache`** — the same discovery mechanism `ComponentTypeResolver.cs` uses for component types.
- **`specs/needs_research/multi-scene-additive.md`** — the explicit boundary: additive/cross-scene
  semantics are NOT in this milestone (see Out of scope).
- **`specs/needs_research/concurrent-multi-scene-sync.md` (B, deferred)** — concurrent auto-sync of several
  simultaneously-open+dirty scenes: per-object owning-scene attribution and per-scene suppression. This
  milestone (A) is its foundation — B builds on the router + single-active routing shipped here.

## Risks / tradeoffs

- **Two open scenes both dirty in one debounce window is B, deferred.** Under single-active scope,
  scene→code routes the active scene (`GetActiveScene()`, `:410`) to its builder; it does NOT attempt to
  attribute each changed `EntityId` to its owning `GameObject.scene`. Concurrent per-scene attribution is
  the deferred B milestone (`specs/needs_research/concurrent-multi-scene-sync.md`).
- **`ExecuteCodeToScene` today is a silent DROP, and the fix is a routing table, not a one-liner.** The
  early-return at `SceneBuilderAutoSync.cs:525-528` fires whenever the changed file ≠ the single computed
  `fullBuilderPath`. Deleting the gate is not enough: the executor then still builds into
  `GetActiveScene()` (`:502`) with the last-built builder's paths, i.e. it would build the WRONG builder's
  code into whatever scene is active. Correct behavior requires (a) mapping each changed file to its own
  `BuilderRoute`, (b) resolving that route's OWN scene (not the active scene), and (c) confirming that scene
  is open. That is the `SceneBuilderRouter` table above — a real per-builder lookup on both directions.
- **A builder whose scene is not open.** Scene→code has nothing to read, so skip. Code→scene could load the
  scene, but auto-opening scenes under the user is intrusive; default to a located skip-log and leave
  on-demand loading to the additive milestone. Pinned by checklist #5.
- **Suppression granularity is B.** Global `SceneWriteSuppressed` (`:256`) is correct while one scene syncs
  at a time (this milestone's scope), so it is kept unchanged. Making it per-scene — so `SuppressScene()`
  (`SceneBuilderBuild.cs:198`) writing scene `A` cannot deafen a genuine concurrent edit to scene `B` — is
  deferred to `specs/needs_research/concurrent-multi-scene-sync.md`.
- **Watching N builders.** The single `FileSystemWatcher` already watches the whole `SceneBuilders/`
  directory for `*.cs` (`SceneBuilderAutoSync.cs:146-150`), so N builders cost no extra watchers — only the
  per-event `TryRouteBuilderFile` lookup, which is O(routes) over a small set. `Discover()` should be cached
  and invalidated on domain reload (TypeCache is reload-scoped anyway), not recomputed per event, to keep
  the debounce cheap (CLAUDE.md sync-performance constraint).
- **Stale routes.** A builder file deleted or a scene renamed leaves a route pointing at a missing path;
  `Discover()` skips builders whose `.cs` is absent, and `TryRouteScene`/`TryGetOpenScene` fail closed
  (drop, log) rather than building into the wrong scene.
