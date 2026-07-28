---
name: unity-live-verify
description: Live-editor validation for CodeScenes. Runs the hard gate (./verify.sh) and then drives a running Unity editor through the Unity CLI to confirm a feature's code<->scene sync actually works live and the console is clean, not just that batchmode EditMode tests pass. Use after building a Unity-facing milestone, or to validate a specific shipped feature against a real editor.
---

# unity-live-verify

Two layers. `./verify.sh` is the authoritative pass or fail. The live-editor round-trip is the extra
confidence the batchmode gate cannot give, because the gate calls the sync entry points directly and never
exercises the real running editor with its file watcher, domain reloads, and `ObjectChangeEvents`.

This has been proven working end to end (2026-07-28): a CLI-driven scene edit synced to the builder `.cs`
in about 3 seconds, and a builder edit synced to the scene in about 3 seconds, with no console errors.

## Layer ordering

1. `./verify.sh` first. If not `GATE PASS`, stop and report. Do not launch the live editor for code that
   already fails the deterministic gate.
2. The live round-trip second, only if layer 1 is green.

The single Unity instance lock forces this order: `verify.sh` opens `unity-gate` in batchmode, the live
check needs its own running editor, and they cannot run at once.

## Policy

`verify.sh` is the pass or fail. The live result is reported alongside but does not red the milestone yet
(the tooling is beta). Surface console errors and round-trip mismatches prominently; a human judges.

## Prerequisites

- **Unity CLI installed and signed in.** `unity --version` and `unity auth status` both work. Install:
  `curl -fsSL https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.sh | UNITY_CLI_CHANNEL=beta bash`
  (binary at `~/.local/bin/unity`; put it on PATH).
- **The pipeline package cached in the target project (ONE-TIME, needs a human once).** The editor's
  Package Manager must download the experimental `com.unity.pipeline`, and CLI auth does NOT satisfy that.
  A human must, once, open the project through the Unity Hub (signed in) so the editor downloads it. You
  know it worked when `<project>/Library/PackageCache/com.unity.pipeline@<hash>` exists. After that, an
  automated `unity open` reuses the cached package with no auth. If it is not cached and no editor is
  connected, stop and ask the human to prime it via the Hub; do not try to hack the Hub token store.
- **A connected editor, or the ability to launch one.** `unity status --json` shows connected editors
  (port, project, pid, state). If one is already open on the target project, USE IT (do not launch a
  second; single-instance). If none and the package is cached, launch your own (see below).

## Getting a connected editor

- Check first: `unity status --json`. If `count > 0` and the instance is the target project in state
  `ready`, use it.
- Otherwise launch Model A (your own automated editor), only if the package is cached:
  `DISPLAY=:1 unity open "$PROJ" --non-interactive --args "-logFile <log> -automated"` in the background,
  then poll `unity status --json` until `count > 0`. `-automated` suppresses modals. Bound the poll and
  abort on: the UPM auth modal (`not signed in` / `Cannot install package` in the log), a compile error
  (`error CS`), or the editor process exiting. Budget several minutes for the first open.
- Do not run while a human has that project open interactively (single instance, and a personal license
  may allow only one editor seat). If you launched the editor, shut it down at the end.

## Driving the editor: the command surface

`unity command <name> [--arg value ...] [--json]` runs a built-in command; `unity list` enumerates them.
There is NO arbitrary C# eval and NO built-in setter for an individual serialized field, so field-level
scene edits need either a settable command that happens to cover the field or a future `[CliCommand]`
harness. What exists and is enough for structural round-trips:

- Read: `list_open_scenes`, `get_component_properties --target <handle|path> --type <T>`, `search --query`,
  `read_text_file --path` (confined to the authoring root under `Assets/`), `get_authoring_root`,
  `recompile_status`.
- Structural write (these are what CodeScenes syncs back to code): `create_gameobjects --name --count
  [--primitive --parent --positions ...]`, `delete_gameobject --target`, `set_parent --target --parent`,
  `set_active --target --active`, `attach_script --target --type|--script`.
- Assets/scenes: `create_scene --path`, `create_asset`, `import_asset --source --path` (copies an external
  file in and imports it), `package_add/remove/resolve`.

Read the builder `.cs` and scene `.unity` files directly from the filesystem with your own tools; they are
outside the authoring root so `read_text_file` will not reach the builder.

## The round-trip pattern (per feature)

CodeScenes' own auto-sync does the round-trip; you drive one side and observe the other. Auto-sync is ON in
the editor by default and fires in about 3 seconds.

- **scene -> code:** make a structural scene edit with a pipeline command (e.g. `create_gameobjects`),
  then poll the builder `.cs` on disk until it reflects the change (or times out). Then reverse the edit
  to clean up.
- **code -> scene:** edit the builder `.cs` on disk (add or change a statement), then poll the scene via a
  read command (`list_open_scenes` rootCount, `search`, `get_component_properties`) until it reflects the
  change. Then restore the builder.

Assert both the change appearing AND the console staying clean (check `recompile_status` and the editor log
for `error CS` / exceptions). Grounded truth only: quote the actual synced line and the read result. If the
editor never connected or the sync never fired, say so; never infer a pass.

## Isolation and safety (critical)

- Simply opening the editor makes auto-sync reconcile any pre-existing code/scene drift, which itself edits
  the governing builder. Expect that.
- Back up any file you will touch, and restore it at the end (sha/byte-verify the restore). Verify the
  scene is clean (root count and object list back to baseline) and no probe objects remain.
- If you installed the pipeline package into a shared project, restore its `manifest.json` and
  `packages-lock.json` and remove any stale `Temp/UnityLockfile` when done.

## Project-specific realities (manual test project, verified 2026-07-28) — read before designing a test

- **Auto-sync is single-builder, hardwired to `DemoScene`.** `SceneBuilderAutoSync.BuilderName = "DemoScene"`;
  the running editor DROPS any file-watcher event whose path is not that builder, and the menu items are
  literally `Build DemoScene` / `Sync DemoScene`. So a dedicated `LiveVerify_<feature>` builder is NOT picked
  up here and an isolated-scene approach does not work. The practical path is a MINIMAL, reversible edit to
  `DemoScene.cs` with a backup, then restore byte-for-byte. Do not burn time trying to bind a new builder.
- **The live read is the source of truth, not the on-disk `.unity` bytes.** FitSize/SurfaceSnap re-apply in
  edit mode, so a build writes authored transforms to the file that the live scene immediately re-snaps in
  memory (e.g. Crate `y=3/scale1` on disk vs `y=0.6/scale1.2` live), and the `.sbmap.json` sidecar gets
  deterministically re-enriched by any build. Diffing the scene file reads as false churn. Assert with
  `get_component_properties` against the live object.
- **Construct a typed catalog member from the manifest, do not guess it.** The chain is
  `Assets.<Group>.<folderSegments…>.<Leaf>`, so `Assets/Materials/Green.mat` is
  `Assets.Materials.Materials.Green` (group and folder both "Materials"), NOT `Assets.Materials.Green`. Read
  `SceneBuilders/Generated/Assets.sbassets.json` (each entry's group + folderSegments + memberName) and the
  emit shape in `CodeScenes.Analyzers/AssetCatalogEmit.cs`.
- **Make a resolution test decisive:** swap the value to a DIFFERENT asset first (observe the live object
  change), then to the target, then back. A parse-failure no-op leaves the pre-existing value in place and
  is otherwise indistinguishable from a successful resolve.
- **`recompile_status` stays `idle` for builder-only edits** (the builder lives outside `Assets/`; typed-chain
  resolution is the CodeScenes Roslyn parser, not Unity's compiler). "Compiles clean" here means no located
  parse diagnostic and a successful `[SceneBuilder] Built in place` log, plus zero `error CS` from the
  source generator compiled into `Assembly-CSharp`.

## What is and is not testable in a given project

Depends on fixtures. Confirmed for the manual test project at `/home/paul/Source/Unity/SceneBuilderTest`
(2026-07-28): the typed catalogs are compile-wired (`Assembly-CSharp.csproj` injects `CodeScenes.Analyzers`
plus `Assets.sbassets.json` / `Prefabs.sbfacade.json` / `ProjectCatalog.sbcatalog.json`), and materials
exist, so typed asset catalogs are testable now. But zero prefabs exist and there is no create-prefab
command, so any prefab-instance feature (overrides, nested overrides, façade selectors, child selectors)
requires creating a prefab fixture first (write the `.prefab` asset and import it, or have a human make it).
Establish the fixture before asserting the feature.
