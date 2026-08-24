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
(`com.codescenes/Editor/`), is THE one place a code→scene (and code→prefab) build outcome is recorded.
It is placed at the shared build core, `SceneBuilderBuild.RunCore`
(`com.codescenes/Editor/SceneBuilderBuild.cs:186`) — the single method every scene build
(`SceneBuilderBuild.Run` is a thin wrapper over it, `:335`) and every prefab build
(`PrefabBuildSyncTarget.Build`, `:253`) delegates to. `RunCore` records at each of its outcomes: its two
refusal returns (`:219` validation diagnostics, `:233` bootstrap conflicts) call `RecordRefused`, its
success return (`:325`) calls `RecordClean`, and a `ParseException` thrown by the raw parse (`:209`,
previously uncaught and propagated past every return) is now caught inside `RunCore`, recorded via
`RecordRefused`, and returned as a refused `BuildResult` — so the thrown and the collected refusal
modalities converge on one recorded shape. Because the recorder lives inside `RunCore`, every current
and future caller inherits it by construction with nothing to opt into: `ExecuteCodeToScene`
(`SceneBuilderAutoSync.cs:684`), the both-changed code-only tail (`ExecuteBothChanged`, `:820`), the
manual menu path (`SceneBuilderBuild.BuildRoute`, `:146`), and — the channel that is silent today —
`SceneBuilderResync.RunMaterialize` (`:102`, focus-regain / scene-open / domain-reload), which discards
the `BuildResult` and drops its diagnostics entirely (a thrown `ParseException` there is swallowed by the
`ResyncRoute` outer catch as a raw unlocated `Debug.LogException`). The prefab code→asset channel
(`ExecutePrefabCodeToAsset` → `PrefabBuildSyncTarget.Build`) inherits it through `RunCore`, and its two
**pre-`RunCore`** refusal returns (multi-root, `PrefabBuildSyncTarget.cs:226`; SB2402
root-name/filename mismatch, `:248`) route their refused `BuildResult` through the same recorder factory,
since they mint an outcome before `RunCore` is reached. The recorder keys on `BuilderName`, which at this
layer is `Path.GetFileNameWithoutExtension(builderPath)` — the exact derivation the router uses
(`SceneBuilderRouter.cs:103`) — so no `BuilderRoute` need be threaded in. It owns
`RecordRefused(builderName, Diagnostic /* or located ParseException data */)` and
`RecordClean(builderName)`. State persists in `SessionState` (survives domain reload, dies with the
editor session, needs no file under `SceneBuilders/` that would re-trigger the watcher), the same
mechanism `LicenseStore` uses (`Licensing/LicenseStore.cs:44-62`). The bare per-site `Debug.LogError`
calls (`SceneBuilderAutoSync.cs:685-702`, `:821-826`, `SceneBuilderBuild.cs:147-150`,
`SceneBuilderAutoSync.Prefab.cs:125-134`) are replaced by the recorder's single-sourced located line,
always carrying `File`, `Line`, `Col`, `Code`/kind, `Message`, and the offending call text.

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
  `ConflictSurfacing` today has `RegisterOverlay(string key)` (instance, `:137`) over a shared
  `static HashSet<string> _registered` (`:20`) and only a `static Clear()` (`:140`) that wipes ALL
  entries — there is NO per-key un-register, so `RecordClean` cannot drop one builder's overlay entry
  without clobbering unrelated conflict overlays. Add `public static void RemoveOverlay(string key)`
  (a per-key removal) plus a static register path (a static `RegisterOverlay` overload, or register via
  the `new ConflictSurfacing()` instance the both-changed path already constructs at
  `SceneBuilderAutoSync.cs:813`) so the static recorder can register on refusal and remove exactly its
  own key on recovery. `Clear()`'s wipe-all semantics stay unchanged for the converged-cycle reset.
- **Cleared on recovery.** A subsequent clean build of the same builder calls `RecordClean`, which drops
  the standing state, un-registers ONLY that builder's overlay entry (via the new `RemoveOverlay`), and
  updates the menu item. After the author fixes the offending line, the next otherwise-valid edit builds
  and the error state disappears with it, leaving any unrelated conflict overlay intact.

**INVARIANT.** No `BuildResult` carrying a refusal can be produced without the recorder observing it,
and a clean build always clears any standing refusal for that builder.

**MECHANISM (of the invariant).** `RunCore` yields its outcome only through the recorder: every `return`
in `RunCore` is `return SceneBuilderBuildStatus.Record*(builderName, ...)`, and the `ParseException` path
is caught inside `RunCore` and routed through the same recorder. `Run` forwards `RunCore`'s return
verbatim, so no scene-side channel can obtain a `BuildResult` the recorder did not see. The two prefab
pre-`RunCore` refusals (`PrefabBuildSyncTarget.cs:226,248`) call the same recorder factory.

**CHECK that fails on bypass (NOT a literal scan).** A source scan for the old formatted refusal
literals is structurally blind: `RunMaterialize` discards its result and contains no refusal literal, so
it could stay dark while the scan reports GREEN — exactly gap #3. Replace it with a behavioral EditMode
check that drives the actual channels: a refusal delivered through the Materialize/resync direction
(`SceneBuilderResync.RunMaterialize`, the previously-silent channel) sets the standing status with file,
line, and `BuilderName` and leaves the scene untouched — asserted by driving a broken builder through
`ResyncRoute`/`RunMaterialize`, not by scanning source; and each remaining channel (auto code→scene, the
both-changed tail, the menu path, and prefab code→asset including a pre-`RunCore` name-mismatch refusal)
sets the standing status on refuse and clears it on the next clean build. A channel that fails to record
therefore fails a test, whereas the old scan passed it.

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
- A refusal delivered through the Materialize/resync direction (focus-regain, scene-open, or
  domain-reload via `SceneBuilderResync.RunMaterialize`) sets the same standing status — file, line,
  `BuilderName` — as a refusal on the auto code→scene path; this channel drops its diagnostics entirely
  today and is the specific regression the fix must close.
- A code→prefab refusal, including a pre-`RunCore` refusal (multi-root, or SB2402 root-name/filename
  mismatch), also sets the standing status rather than only logging a console line.
- `RecordClean` for one builder clears only that builder's overlay entry (via `RemoveOverlay`) and leaves
  any unrelated conflict overlay registered by `ConflictSurfacing` intact.
- A regression test asserts the recorded/surfaced diagnostic carries builder file, line, and builder
  identity (`BuilderName`), and that a refusal sets the standing status while a following clean build
  clears it. Because the refusal is produced against a real builder file routed to a live editor scene,
  this is an EditMode test in `unity-gate/Assets/GateTests/` (a builder whose `.On(...)` line is
  unsupported → status set with file+line+identity, scene untouched; fix the line → build succeeds →
  status cleared), the boundary the headless Core suite is structurally blind to. The SAME test drives
  the previously-silent Materialize/resync channel (a broken builder through `ResyncRoute`/`RunMaterialize`
  sets the standing status) — the behavioral check that replaces a literal source scan, which is blind to
  a channel with no refusal literal.
