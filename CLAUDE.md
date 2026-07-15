# SceneBuilder — operating contract for all agents

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

## Hard requirement: Unity-facing changes need EditMode coverage

Any change to `com.scenebuilder/` (the Unity adapter/runtime) or to Unity-observable behavior is
**not complete** without an EditMode test in `unity-gate/Assets/GateTests/` that exercises the real
behavior against a live editor scene (real `GameObject`/`SerializedProperty`/`GlobalObjectId`/
`AssetDatabase`). The headless Core tests operate on POCO fixtures and are structurally blind to the
Unity boundary — that boundary is where bugs escape, so it must be covered by a test that actually
runs Unity. The pipeline decomposition MUST include such a test for adapter work, and `./verify.sh`
enforces it.

## Everything is built and validated THROUGH the tdd-pipeline

Core AND adapter both go through the tdd-pipeline with `./verify.sh` as the gate. Do NOT hand-wire
adapter code or ship "compile-only" adapter changes outside the pipeline — the split that skipped
real Unity validation is exactly what leaked bugs. If the adapter isn't structured to be testable in
EditMode, refactor it (through the pipeline) so it is.

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
