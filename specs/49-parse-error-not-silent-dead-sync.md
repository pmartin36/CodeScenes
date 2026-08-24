# Spec 49: a code→scene build error is a discoverable state, not a lost log line

One defect, observed live during a one-shot authoring run (SceneBuilderOneShot, Claude session
`a6792c4f`): when the builder file carries a parse or planning-phase error, the code→scene cycle
correctly refuses the whole build and leaves the scene untouched, but its ONLY signal is a single
`Debug.LogError` line emitted once per cycle. That line is indistinguishable, from the author's chair,
from "auto-sync stopped running": both look like "I edited the code and nothing happened to my scene."
The author spent a long stretch believing auto-sync had died before discovering a single bad statement
(`.On(...)` at line 71) was refusing the entire Hole-2 build. Compounded by a stale-console tooling
issue that made even the one logged line unreliable to see. The whole-scene refusal is correct and
stays (§4/§7 REFUSE, never guess); the bug is that the refusal has no durable, glanceable state an
author can find, so a valid-but-refused build reads as a dead loop. This spec adds that state; it
changes no parse, plan, or emit behavior.

## The measured defect

Reproduced during real authoring, auto-sync on. A single unsupported call in the builder:

```
ParseException: Unsupported builder call '.On(...)' at line 71
```

aborts the ENTIRE scene build. In the code the observed message originates as diagnostic **SB1002**
`Unsupported builder call '.{method}(...)'` (`SceneBuilder.Grammar/FlatShapeRecognizer.cs:210`),
collected by the non-throwing collect-all planning walk into `BuildResult.Diagnostics`, whose own
contract states "Non-empty means the Build REFUSED (scene untouched)"
(`com.codescenes/Editor/SceneBuilderBuild.cs:56-57`).

The code→scene executor owns the surfacing. `SceneBuilderAutoSync.ExecuteCodeToScene`
(`com.codescenes/Editor/SceneBuilderAutoSync.cs:660-704`) does log the refusal with full location:

```
// SceneBuilderAutoSync.cs:685-690  (diagnostic refusal)
Debug.LogError(
    $"[CodeScenes] {diagnostic.Code} {diagnostic.File}({diagnostic.Line},{diagnostic.Col}): " +
    $"{diagnostic.Message} — scene left untouched.");

// SceneBuilderAutoSync.cs:697-702  (thrown ParseException catch)
Debug.LogError(
    $"[CodeScenes] Parse error in {route.BuilderPath} at line {e.Line}, column {e.Column}: " +
    $"{e.Message} — scene left untouched.");
```

The `Diagnostic` carries `File`, `Line`, `Col`, `Code`, `Message`
(`SceneBuilder.Core/Validation/Diagnostic.cs:8-26`), so the line already names file, position, and the
offending call. Location is not the gap. The gap is threefold:

1. **Ephemeral.** The signal is a `Debug.LogError` and nothing else. It scrolls away, is buried among
   unrelated console output, and (per the observed stale-console tooling issue) may not be visible at
   all. There is no persistent per-builder "this builder currently refuses to build" state anywhere.
2. **Indistinguishable from a stalled sync.** The persisted master toggle
   (`com.codescenes/Editor/SceneBuilderAutoToggle.cs`) is the only durable state an author can consult,
   and it reports one fact: auto is on or off. It cannot express "auto is ON and the governing builder
   is refusing at File:Line." So a running-but-refusing loop and a not-running loop present identically:
   an unchanged scene.
3. **Surfaced per call site, not once.** The same one-shot refusal log is duplicated across the
   code→scene channels: the auto path above, the both-changed code-only tail
   (`SceneBuilderAutoSync.cs:819-826`), and the manual menu path
   (`com.codescenes/Editor/SceneBuilderBuild.cs:147-151`). None records a standing state; each just logs
   and returns. A fix wired into one of them would leave the others still going dark.

## The fix

**OWNER.** A single per-builder build-status recorder, `SceneBuilderBuildStatus`
(`com.codescenes/Editor/`), is THE one place a code→scene build outcome is recorded, and every refusal
channel funnels its result through it rather than logging in isolation. It owns two operations keyed on
`BuilderRoute.BuilderName`: `RecordRefused(builderName, Diagnostic)` (or the located `ParseException`
data) and `RecordClean(builderName)`. State is persisted in `SessionState` (survives domain reload,
dies with the editor session, needs no file under `SceneBuilders/` that would re-trigger the watcher),
mirroring how `SceneBuilderAutoToggle` persists in `EditorPrefs`. Placing the recorder at the shared
outcome point, not behind an opt-in each executor remembers to call, is required by CLAUDE.md: every
current and future caller of the code→scene path inherits it. The three call sites
(`SceneBuilderAutoSync.cs:685-702`, `:821-826`, `SceneBuilderBuild.cs:147-151`) route their
refuse/clean outcome through the recorder instead of a bare `Debug.LogError`; the located Console error
stays (it is the fail-loud line), but it is now emitted by the recorder so its format is single-sourced
and always carries `File`, `Line`, `Col`, `Code`/kind, `Message`, and the offending call text.

**MECHANISM.** The recorded state is surfaced where the author already looks for sync state, alongside
the master toggle, as a distinct fact:

- **A standing status menu item under `CodeScenes/`** (sibling of `CodeScenes/Auto-sync`), disabled and
  reading `No build errors` when every governing builder last built clean, and enabled and reading
  `Build error: <BuilderFile>:<Line>` when any builder's last outcome was a refusal. Invoking it pings
  the builder file at the offending line (`AssetDatabase`/`InternalEditorUtility.OpenFileAtLineExternal`
  or equivalent). This is the persistent, glanceable state that a scrolling console line is not, and it
  is orthogonal to the toggle: toggle answers "is auto on," status answers "is the builder refusing."
- **The existing scene-view overlay** (`ConflictSurfacing`'s registry + `OnSceneGui`,
  `com.codescenes/Editor/ConflictSurfacing.cs:136-204`) registers a "build error at <File>:<Line>" entry
  while a refusal stands, so the state is visible in the scene view the author is editing, and clears on
  the next clean build. Reusing this registry keeps one durable-surface mechanism, not a second one.
- **Cleared on recovery.** A subsequent clean build of the same builder calls `RecordClean`, which drops
  the standing state, un-registers the overlay entry, and updates the menu item. After the author fixes
  the offending line, the next otherwise-valid edit builds and the error state disappears with it.

Whole-scene refusal is unchanged: a parse/plan error still leaves the scene untouched, and no builder
with a broken statement is ever partially applied. Only the signal changes.

## Accept when

- A governing builder with exactly one invalid call (e.g. an unsupported `.On(...)`) yields a single,
  clearly located error naming the builder file, the line, and the offending call, and the scene is left
  untouched.
- That refused state is durably discoverable and distinguishable from a stalled/off sync: with auto ON,
  the `CodeScenes/` status surface reports `Build error: <BuilderFile>:<Line>` (and the scene-view
  overlay shows the standing error), whereas a clean builder reports no build error; the state persists
  across a domain reload within the session rather than living only in a console line that can scroll away
  or be lost to a stale console.
- After the author fixes the offending line, an otherwise-valid edit to the same builder builds
  successfully and the standing error state (menu item and overlay) clears.
- A regression test asserts the recorded/surfaced diagnostic carries builder file, line, and builder
  identity (`BuilderName`), and that a refusal sets the standing status while a following clean build
  clears it. Because the refusal is produced against a real builder file routed to a live editor scene,
  this is an EditMode test in `unity-gate/Assets/GateTests/` (a builder whose `.On(...)` line is
  unsupported → status set with file+line+identity, scene untouched; fix the line → build succeeds →
  status cleared), the boundary the headless Core suite is structurally blind to.
