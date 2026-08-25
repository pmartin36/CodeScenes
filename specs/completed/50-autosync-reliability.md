# Spec 50: auto-sync reliability — scene-open race, atomic-write blindness, and pump death

Three transport-layer defects in the auto-sync loop, all observed live in a one-shot authoring run
(SceneBuilderOneShot, Claude session `a6792c4f`), that each made a WORKING build look broken and cost
multiple diagnosis cycles. None corrupts source (spec 46's territory); each instead silently drops a
sync that should have run — a code->scene build issued while its scene is still opening, an external
edit written by atomic-replace, and every cycle after a play-mode or probe round trip. CLAUDE.md rates
seamless non-user-driven sync THE product: "Sync fires on EVERY change... They never press a button."
A sync that is skipped, missed, or dead-after-play-mode violates that directly. All three owners are in
`com.codescenes/Editor/SceneBuilderAutoSync.cs`; each fix belongs on the one path the trigger cannot
bypass, and each is covered by an EditMode test against a live editor scene.

## The measured defect

Reproduced during real authoring (auto-sync on), Console verbatim where quoted.

1. **The code->scene cycle races scene-open and drops the edit.** A builder file was edited while its
   target scene was still opening. Console:
   `scene Assets/SceneBuilder/MiniTest.unity is not open — code->scene build skipped.` The edit was not
   deferred; it was discarded. `PumpOnce` copies and CLEARS `_pendingSourcePaths` and disarms the
   deadline BEFORE dispatch (`SceneBuilderAutoSync.cs:444-448`), then `ExecuteCodeToScene`
   (`:660`) calls `SceneBuilderRouter.TryGetOpenScene` (`:674`), which requires a scene whose
   `isLoaded` is true and whose path matches the route (`SceneBuilderRouter.cs:172-187`). During open
   the scene is absent or not-yet-loaded, so the guard fails, the handler logs and `continue`s
   (`:676-680`), and the already-cleared path is gone. There is no re-enqueue: the build never runs,
   even though the scene finishes opening a tick later.

2. **The watcher misses atomic-replace writes.** An in-place append (`printf >>`) fired the
   FileSystemWatcher and synced; a save written via temp-file + rename (the atomic-replace path most
   editors and tools use) did not, so the edit silently never reached the scene. The watcher is
   constructed with a server-side name filter `"*.cs"` (`SceneBuilderAutoSync.cs:209`, prefab lane
   `:222`). An atomic replace writes a temp file whose name does NOT match `*.cs`, then renames it onto
   the builder path; on the Linux inotify backing, the rename's from-half (the temp) has no matching
   watch, so the paired event for the final `.cs` is not reliably delivered and `DrainWatcherPaths`
   (`:474`) never calls `NotifySourceChanged` (`:372`) for it. An append mutates the existing inode in
   place (a plain `Changed`) and fires. The only backstop, focus-regain resync (`OnFocusChanged`,
   `:171-177` -> `SceneBuilderResync.ResyncActiveScene`, `SceneBuilderResync.cs:106`), is gated on an OS
   focus toggle and covers only the active scene's route — an external atomic save while Unity holds
   focus triggers nothing.

3. **The update pump dies after a play-mode or probe round trip.** After entering and leaving play
   mode (and after a Unity Pipeline probe/eval session), auto-sync stopped firing entirely; it came
   back only after a manual domain reload (touch a script under `Assets/`). The pump is
   `EditorApplication.update += OnUpdate`, subscribed in `Arm` (`:147`) and removed in `Disarm`
   (`:162`). `OnPlayModeStateChanged` (`:188-199`) disarms on `EnteredPlayMode` (`:193`) and re-arms on
   `EnteredEditMode` by a single synchronous `ApplyToggleState()` (`:196`), whose guard requires
   `!EditorApplication.isPlayingOrWillChangePlaymode` (`:127`). Read inside that callback the flag can
   still be true, so `ApplyToggleState` takes the `Disarm` branch and the pump stays removed; nothing
   re-invokes it (the update tick that would is exactly what was unsubscribed). The static-ctor re-arm
   (`:35-40`) is no backstop when a probe or "Reload Domain"-off play session does not reload the
   domain, so the ctor never re-runs and `OnPlayModeStateChanged` is the sole re-arm — one shot, no
   retry.

## The fix

Each fix lands on the one path every trigger routes through, per CLAUDE.md's "every current and future
caller inherits it by default — never behind an opt-in."

1. **OWNER: the code->scene dispatch path (`PumpOnce` `:442-448` + `ExecuteCodeToScene` `:660`).**
   A code->scene cycle whose target scene is not yet open must DEFER, not drop. Mechanism: when
   `TryGetOpenScene` misses, re-enqueue the path and re-arm `_sourceDeadline` (the same pending-set the
   pump drains) instead of logging-and-continuing, so the next pump tick after the scene finishes
   opening runs the build. The re-drive is owned by the pump's own pending set, so the scene-open hook
   (`OnSceneOpened`, `:180`) and a later tick both converge on it; no caller can issue a build that a
   still-opening scene silently swallows. A retry ceiling avoids an unroutable path spinning forever.

2. **OWNER: the source watcher construction + drain (`StartWatcher` `:201-233`, `DrainWatcherPaths`
   `:474`).** A rename INTO a governing builder path must be seen regardless of the temp name it was
   renamed from. Mechanism: drop the constructor's `"*.cs"` server-side name filter (watch the
   directory unfiltered) and filter by `.cs` extension on the main thread in `EnqueueWatcherPath` /
   `DrainWatcherPaths`, so the inotify move-into-place event for the final `.cs` is delivered and routed
   through the existing `NotifySourceChanged` lane. Backstop for any write that still slips the watcher:
   a content-hash rescan of every discovered builder path, owned by the pump and driven on focus-regain
   (widening `OnFocusChanged` beyond the active-scene resync), so a missed atomic write converges on the
   next focus without a keystroke. `NotifySourceChanged`'s own-write guard (`DropsAsOwnWrite`, `:391`)
   already suppresses our echoes, so the rescan cannot loop.

3. **OWNER: the play-mode re-arm (`OnPlayModeStateChanged` `:188-199` + `ApplyToggleState` `:125`).**
   The pump must be live after every play-mode and probe round trip with no manual reload. Mechanism:
   the `EnteredEditMode` re-arm defers to `EditorApplication.delayCall += ApplyToggleState` rather than
   calling it synchronously, so the toggle re-evaluates after the transition settles and
   `isPlayingOrWillChangePlaymode` reads false; plus a self-healing watchdog on a class-lifetime hook
   (like `playModeStateChanged`, bound to the static ctor, not to Arm/Disarm) that re-arms whenever the
   toggle is on, the license allows, and play mode is off, but the pump is not subscribed. The watchdog
   must not itself depend on the `EditorApplication.update` pump it restores, since that pump is what
   dies.

## Accept when

An EditMode test in `unity-gate/Assets/GateTests/` proves each, against a real editor scene, per
CLAUDE.md's hard requirement that Unity-observable behavior carries EditMode coverage.

1. A source change is enqueued for a builder whose scene is NOT among the open scenes; a pump tick runs
   and the build is deferred, not dropped (no permanent skip, path still pending). The scene is then
   opened and a further tick runs: the code->scene build now executes and the scene reflects the edit.
   A permanent guard asserts a not-open code->scene cycle leaves its path pending rather than clearing
   it.

2. A builder file changed by an atomic replace (write a temp file, then `File.Replace`/rename it onto
   the builder path) triggers a code->scene cycle and the scene converges to the new source — parity
   with the append case, which the same test also drives. A focus-regain after a slipped atomic write
   converges the active scene's builder with no in-scene edit.

3. After an `EnteredPlayMode` -> `EnteredEditMode` round trip driven through `OnPlayModeStateChanged`,
   `SceneBuilderAutoSync.IsArmed` is true and a subsequent scene edit still fires a scene->code cycle
   (cycle count increments) with no manual domain reload. A probe-shaped round trip (no domain reload
   between the transitions) leaves the pump armed by the same assertion.
