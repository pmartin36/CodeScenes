# Spec 55: a menu to bind a builder to the current scene

A builder binds to a scene by its filename plus the `Scene` path recorded in its sidecar
(`SceneBuilders/<Name>.sbmap.json`, `IdentityMap.Scene`, `SceneBuilder.Core/Identity/IdentityMap.cs:12`);
the default is `Assets/SceneBuilder/<Name>.unity`. Today the ONLY way to point a builder at an existing
scene at any other path is to hand-write that JSON. This spec adds the one-click human equivalent: with a
saved scene open, bind a chosen builder to it (record the scene's path in the builder's sidecar) so the
existing auto-sync / Build flow reconciles the builder's code into that already-existing scene,
non-destructively. Adapter-only — no Core changes; it writes an existing field via existing machinery.

## OWNER

A single factored action, `SceneBuilderBind.BindSceneToBuilder(string builderName, string scenePath,
bool overwriteExisting)` in a new `com.codescenes/Editor/SceneBuilderBind.cs`
(namespace `SceneBuilder.Editor`), is THE one writer of a builder's sidecar `Scene` field for this
adopt-existing-scene path. A `CodeScenes/Bind Current Scene To/<BuilderName>` dynamic submenu — one entry
per `SceneBuilderRouter.Discover()` route, registered via the reflection `AddMenuItem` mechanism already
used at `com.codescenes/Editor/SceneBuilderBuildStatusMenu.cs:125-126` — is a thin wrapper that resolves
the active scene's path and, on a would-clobber, confirms via `EditorUtility.DisplayDialog` before calling
the action with `overwriteExisting: true`. The action is `internal static` and menu-independent so an
EditMode test drives it directly (menu items themselves are not unit-testable).

## MECHANISM

- **Explicit builder pick, never a guess.** Builder `.cs` files live outside `Assets/`
  (`com.codescenes/Editor/SceneBuilderPaths.cs:19-32`), so they are not selectable Project assets —
  `Selection.activeObject` cannot target them. The submenu offers one entry per discovered builder
  (`SceneBuilderRouter.Discover()`, `SceneBuilderRouter.cs:85-116`); the user picks. A filename match
  (scene stem == builder name) is at most an ordering hint, never a silent auto-target — the whole point
  is that the scene sits at a path whose name need not equal the builder's.
- **Untitled-scene guard.** Each entry's validate delegate returns
  `LicenseGate.Allowed && !string.IsNullOrEmpty(EditorSceneManager.GetActiveScene().path)` (active-scene
  read as `SceneBuilderBuild.cs:91`; `LicenseGate.cs:20`) — an untitled/never-saved scene has an empty
  `scene.path` and nothing to record, so its entries are greyed. The action re-checks the empty path and
  refuses, so a direct caller cannot bypass the greyed menu.
- **Read-modify-write preserving entries.** Load the sidecar with
  `IdentityMapJson.Deserialize(File.ReadAllText(sidecarPath))` when it exists (else `new IdentityMap()`),
  set `map = map with { Scene = scenePath }` (record with-expression preserves `Entries[]`/`Assets[]` —
  `IdentityMap.cs:6,15,18`), and write with `SceneBuilderPaths.WriteIfChanged`
  (`SceneBuilderPaths.cs:211`) so the self-write is recorded under `SuppressionScope` and the code→scene
  watcher does not fire a spurious build on the bind. (`WriteTextIfChanged`, `:156`, skips that
  registration and must not be used.) Sidecar path via `SceneBuilderPaths.Sidecar(builderName)` (`:44`).
- **No silent clobber.** When the existing `Scene` is non-empty and differs from `scenePath` and
  `overwriteExisting` is false, the action writes nothing and returns a would-clobber result; the menu
  wrapper then confirms via dialog before retrying with `overwriteExisting: true`.
- **Route refresh.** After a successful write, call `SceneBuilderRouter.Invalidate()`
  (`SceneBuilderRouter.cs:194-198`) so `Discover()`/`TryRouteScene` (`:156-169`) route the builder to the
  new scene immediately (the per-domain cache at `:75,87` is otherwise stale until a domain reload).
- **Hand-off to the existing flow.** Binding is a pure metadata write; it does not mutate the scene. The
  next `CodeScenes/Build/Current Scene` (`SceneBuilderBuild.cs:84`) or auto-sync cycle resolves the builder
  via `TryRouteScene` and runs `SceneBuilderBuild.RunCore`, which reconciles IN PLACE against the live
  hierarchy (`SceneBuilderBuild.cs:261-280`) — user-added objects survive; coded objects are matched by
  `IdentityRemapper.Remap` (`:253-254`).

## Invariant and check

- **INVARIANT.** A bind writes ONLY the `Scene` field of exactly one builder's sidecar, never dropping
  that sidecar's existing `Entries[]`/`Assets[]`; a bind that would overwrite a different already-recorded
  scene writes nothing unless the caller explicitly opted in; and after any successful bind the router
  routes that builder to that scene with no domain reload.
- **CHECK.** A behavioral EditMode test that drives the factored action against the real `SceneBuilders/`
  dir and live scenes (harness per `MultiSceneRoutingTests.cs:43-90` — seed via `SceneBuilderBuild.Run`,
  reset with `SceneBuilderRouter.ResetForTests()` + `LicenseGate.ResetToDefault()`). A writer that dropped
  entries (bypassing the record with-expression), skipped `Invalidate()`, or clobbered a differing scene
  without opt-in fails an assertion below. A source scan is blind to the `Invalidate()` omission and the
  entry-preservation regression, so the check must run the action.

## Accept when

- **Bind writes the path, preserves entries.** Seed a builder sidecar (via `SceneBuilderBuild.Run`) with
  non-empty `Entries[]` and `Assets[]`; call `BindSceneToBuilder(name, existingScenePath, overwrite: false)`;
  the reloaded sidecar has `Scene == existingScenePath` and its `Entries[]`/`Assets[]` are unchanged.
- **Router routes after bind.** After the bind, `SceneBuilderRouter.TryRouteScene(sceneAtThatPath, out route)`
  returns true with `route.BuilderName == name`, with no intervening domain reload (proves `Invalidate()`).
- **Subsequent build is non-destructive.** Open the bound existing scene, add a user `GameObject` the
  builder code does not declare, run `SceneBuilderBuild.Run` for the route; the coded objects appear AND
  the user object still exists (adopt-existing-scene, `SceneBuilderBuild.cs:20-25,261-280`).
- **Untitled-scene guard.** With an active scene whose `scene.path` is empty, the action refuses (writes no
  sidecar) and the submenu validate delegate returns false.
- **Already bound to a different scene.** With the sidecar `Scene` already set to scene A,
  `BindSceneToBuilder(name, sceneB, overwrite: false)` writes nothing and reports would-clobber (sidecar
  unchanged); the same call with `overwrite: true` rebinds to B and preserves `Entries[]`/`Assets[]`.
- All of the above is an EditMode test in `unity-gate/Assets/GateTests/` (the boundary the headless Core
  suite is blind to), with builder `.cs`/sidecars in the real `SceneBuilders/` location via
  `SceneBuilderPaths`, never under `Assets/`.

## Out of scope

- **Creating or opening a scene.** Bind records a path to an existing, saved scene; it never creates or
  opens one, and refuses an untitled scene.
- **Building/populating on bind.** The bind is a pure metadata write; populating is the existing
  Build/auto-sync flow. An optional "Build now?" dialog in the menu wrapper is a UX affordance, not part of
  the action contract.
- **The filename-stem convention.** `<Name>.cs` ⇄ `<Name>.sbmap.json` (`SceneBuilderPaths.cs:41,44`) is
  reused verbatim; no rename support.
- **Prefab builders.** This targets scene builders (`BuilderRoute`); the prefab route
  (`SceneBuilderRouter.cs:45`) is untouched.
- **Rewriting stale `Entries[]` on rebind.** Preserving entries is the contract; entries whose
  GlobalObjectIds belonged to the prior scene simply fail to resolve and reconcile fresh on the next build
  (`SceneBuilderBuild.cs:351-451`) — not a special-cased purge here.
