# SceneBuilder — operating contract for all agents

## The product IS seamless, non-user-driven sync — this is the key value prop

Invisible bidirectional sync: the user edits the scene and the code updates; they edit the code and
the scene updates. **They never think about it and they never press a button.** This is the product,
not a milestone and not a toggle. Every design decision is subordinate to it.

Constraints that follow (they are constraints, not preferences — do not re-litigate them):
- **Sync fires on EVERY change, so it must be effectively instant.** A ~2s domain reload per sync
  means: drag a transform value → freeze → drag another → freeze. That is fatal, not a tradeoff.
- **The builder file MUST NOT live in Unity's compiled asset pipeline.** Any code under `Assets/`
  (a `.cs`, or even a prebuilt `.dll`) triggers a domain reload; Unity cannot reload individual
  assemblies, and `LockReloadAssemblies` / `DisallowAutoRefresh` only defer it. The builder lives
  outside `Assets/` with its own `.csproj` (referencing `SceneBuilder.Authoring` + the Unity managed
  DLLs) so the IDE still provides IntelliSense and type-checking. Unity's own Rider/VS packages do
  exactly this — generated `.csproj`/`.sln` at the project root, no `Refresh()`, no reload.
- **The `Build` / `Sync` menu items are debug scaffolding, not the product.** The happy path has no
  buttons. Never design a feature that depends on the user invoking a menu item.
- **Sync performance is a first-class constraint** — it runs continuously, not on a click. A
  full-scene walk calling `GlobalObjectId.GetGlobalObjectIdSlow` per object is acceptable per
  button-press and fatal per keystroke. Auto-sync requires debouncing + incremental/cached identity.
- **M-Auto is the driver, not a downstream milestone.** Scene→code hooks `ObjectChangeEvents`;
  code→scene needs the plugin's own file watcher (Unity does not watch a builder outside `Assets/`).
  `specs/14-m-auto-live-sync.md` specs it seamless-by-default: auto is **ON**, no buttons on the happy
  path. A single **persisted master toggle** (one switch governing both directions) exists only as a
  testing/power-user affordance — to disable auto and drive sync by hand while validating other
  milestones — and the Build/Sync menu items are retained as those manual/debug tools. Default is ON.
- **Generated C# must compile.** Emitted code is validated by a Roslyn compile assertion in the gate;
  a write path that can emit non-compiling source is a bug, not a style issue.

## The gate is `./verify.sh` — nothing ships without it

`./verify.sh` is THE validation gate. The tdd-pipeline MUST use it as its gate command; a task
cannot be GREEN, and a bucket cannot commit, unless `./verify.sh` exits 0.

It has two layers:
1. **Core (always):** `dotnet build SceneBuilder.sln && dotnet test SceneBuilder.sln` — the fast
   headless suite (seconds). This is the inner loop for pure-Core work.
2. **Unity EditMode (conditional):** the real editor suite in `unity-gate/` (minutes), run whenever
   the change touches `com.scenebuilder/` or `unity-gate/` (or `GATE_FORCE_UNITY=1`). It gates on
   BOTH the process exit code AND the results XML — a missing/failed `results.xml` is a FAILURE,
   never "probably fine". A pure-Core change skips layer 2 and says so; a skip never counts as a
   Unity pass.

### Reading the gate's verdict — the ONLY reliable check is the `GATE PASS` line

`./verify.sh` prints exactly one verdict line: `GATE PASS: ...` or `GATE FAIL: ...`. Read THAT.
**A wrapper's exit code is not the gate's exit code** and has already fooled three separate agents:
backgrounding the gate as `( ./verify.sh > log 2>&1; echo "EXIT=$?" >> log ) &` makes the subshell
exit with the status of the final `echo` — always 0 — so a FAILED gate is reported as "exit code 0".
If you background it, propagate the status explicitly and still read the verdict line:

```bash
( ./verify.sh > "$LOG" 2>&1; code=$?; echo "REALEXIT=$code" >> "$LOG"; exit $code ) &
# then: grep -aE 'GATE PASS|GATE FAIL|REALEXIT' "$LOG"
```
Never report a gate as green on an exit code alone. Quote the `GATE PASS` line or you did not verify it.

## Hard requirement: Unity-facing changes need EditMode coverage

Any change to `com.scenebuilder/` (the Unity adapter/runtime) or to Unity-observable behavior is
**not complete** without an EditMode test in `unity-gate/Assets/GateTests/` that exercises the real
behavior against a live editor scene (real `GameObject`/`SerializedProperty`/`GlobalObjectId`/
`AssetDatabase`). The headless Core tests operate on POCO fixtures and are structurally blind to the
Unity boundary — that boundary is where bugs escape, so it must be covered by a test that actually
runs Unity. The pipeline decomposition MUST include such a test for adapter work, and `./verify.sh`
enforces it.

## How work is built: feature work goes through the tdd-pipeline; everything is gated

- **Feature work — anything with a spec in `specs/` (a milestone, or a new capability) — MUST go
  through the tdd-pipeline**, Core AND adapter alike. Do NOT hand-wire a feature or ship
  "compile-only" adapter changes: the split that skipped real Unity validation is exactly what
  leaked bugs. If the adapter isn't structured to be testable in EditMode, refactor it (through the
  pipeline) so it is.
- **Bug fixes, investigations, and infrastructure may be done by a subagent** working RED-first
  against the same gate. This is the practical path when the pipeline's `Workflow` tool isn't in a
  subagent's toolset.
- **Non-negotiable either way: `./verify.sh` is the gate.** The orchestrator may vary; the gate never
  does. Nothing ships without a quoted `GATE PASS`, and a fix is not done until it ships with a
  regression test. A bug found without a spec is still a bug — write the spec if the fix defines new
  behavior (see `specs/16-duplicate-sibling-identity.md`).

## Layout

- `SceneBuilder.Core/` + `SceneBuilder.Core.Tests/` — Unity-free Core, `dotnet test`. `SceneBuilder.sln` at root.
- `com.scenebuilder/` — the Unity package (Runtime + Editor adapter + `Plugins/SceneBuilder.Core.dll`,
  auto-staged by a Core post-build target — never hand-copy it).
- `SceneBuilder.Editor.CompileCheck/` — compiles the adapter against Unity DLLs for the `dotnet` build.
- `unity-gate/` — the dedicated Unity project the gate runs. Embeds `com.scenebuilder` via a relative
  package ref; EditMode tests live in `unity-gate/Assets/GateTests/`. Do NOT open this project in an
  interactive editor while the gate runs (Unity single-instance lock).
- `specs/` — authoritative milestone specs. Each spec's "Unity confirmation checklist" should become
  EditMode tests in `unity-gate/`.

## Environment

- dotnet at `~/.dotnet` (on PATH via `~/.zshenv`). Unity `6000.5.3f1` at
  `~/Unity/Hub/Editor/6000.5.3f1/Editor/Unity` (override via `UNITY_EDITOR`). Personal license is
  activated (Hub) — batchmode reuses it; the first-attempt `LicenseClient-<user>` handshake warning is
  self-recovered and harmless.
