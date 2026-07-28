---
name: unity-live-verify
description: Milestone-boundary validation for CodeScenes that runs the hard gate (./verify.sh) and then a live-editor round-trip check via the Unity CLI. Use after building a Unity-facing milestone to confirm code<->scene sync actually works in a running editor and the console is clean, not just that batchmode EditMode tests pass. Model A - the skill brings up its own automated editor.
---

# unity-live-verify

Runs two layers in sequence and reports both. Layer 1 is the authoritative pass or fail. Layer 2 is
the live confidence check that the batchmode gate structurally cannot give.

## When to use

At the end of a Unity-facing milestone, after its tdd-pipeline run has committed. Not per task. The live
editor daemon is expensive and beta-flaky, so this is a milestone-boundary step, invoked deliberately.

Run it in a subagent. The editor logs and CLI output are heavy and should not land in the main context.

## Layer ordering (do not reorder)

1. `./verify.sh` first. It is the hard verdict (Core plus batchmode EditMode). If it is not `GATE PASS`,
   stop and report the failure. Do not launch the live editor to round-trip code that already fails the
   deterministic gate.
2. The live-editor round-trip second, only if layer 1 is green.

The single Unity instance lock forces this order anyway: `verify.sh` opens `unity-gate` in batchmode, and
the live check needs its own running editor. They cannot run at the same time, so layer 1 must fully finish
before layer 2 launches.

## Policy: advisory until the beta stabilizes

`verify.sh` is the pass or fail. The live result is reported alongside it but does NOT red the milestone
yet, because the tooling is a beta (`unity` CLI beta, `com.unity.pipeline` experimental) with real
flakiness. Promote the live layer to a hard blocker only once it has proven stable across several
milestones. Until then: run it, surface console errors and round-trip mismatches prominently, let a human
judge.

## Prerequisites (verify before running layer 2; skip layer 2 with a clear note if any fail)

- **Unity CLI installed.** `unity --version` works. Install if missing:
  `curl -fsSL https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.sh | UNITY_CLI_CHANNEL=beta bash`
  (binary lands at `~/.local/bin/unity`; add `~/.local/bin` to PATH).
- **CLI signed in.** `unity auth status` shows a signed-in account.
- **Unity Hub signed in for package downloads.** THIS IS THE KNOWN BLOCKER. The editor's Package Manager
  needs valid Hub credentials to download the experimental `com.unity.pipeline` package. CLI auth is a
  SEPARATE store and is not sufficient. If the Hub session lacks package-registry credentials, the editor
  fails to resolve the package with `Cannot install package. You are not signed in. Please sign in using
  the Hub and retry` and pops a Retry/Continue/Quit modal. There is no documented non-interactive token
  path for the main Unity registry, so a human must sign into the Hub GUI once. If layer 2 hits this,
  report it and stop; do not hack the Hub token store.
- **A dedicated live-verify project.** Not `unity-gate` (that is verify.sh's, driven in batchmode) and not
  the interactive manual test project you sit in. A small project referencing `com.codescenes` by `file:`
  path, holding the builders and scenes that exercise the milestone's features. See "project setup" below.
- **The `[CliCommand]` round-trip harness exists in `com.codescenes`.** NOT YET BUILT. Layer 2 needs a way
  to drive scene edits and read state from the CLI. Two possible routes, decide once an editor is actually
  connected: (a) `unity command` exposes a built-in C# eval, in which case drive edits and reads through
  eval with no custom code; (b) it does not, in which case add `[CliCommand]` methods to `com.codescenes`
  (apply-scene-edit, read-builder-source, force-sync-each-direction, dump-console-since-marker). Building
  that harness is its own milestone; see `specs/needs_research/live-editor-validation-unity-cli.md`.

## Layer 2 steps (Model A: bring up our own automated editor)

Real command surface, confirmed against `unity 1.0.0-beta.3` / `com.unity.pipeline 0.4.0-exp.1`:

1. **Install the pipeline package** into the live-verify project:
   `unity pipeline install --project-path "$PROJ" --non-interactive`
   (writes `com.unity.pipeline` into the project manifest; the editor downloads it on next open, which is
   where the Hub-auth prerequisite bites).
2. **Launch the editor daemon** in the background, automated to suppress modals:
   `DISPLAY=:1 unity open "$PROJ" --non-interactive --args "-logFile <log> -automated"`
   (`-automated` suppresses the modal dialogs that otherwise block an unattended session).
3. **Wait for connection** with a bounded poller that also catches failure, never a bare wait:
   poll `unity status --json` until `count > 0`; abort with a clear message if the log shows the UPM
   auth modal, safe mode, a compile error, or the editor process exits. Budget several minutes for the
   first open (domain reload plus import).
4. **List capabilities:** `unity list` (registered pipeline commands) and `unity command` with no command
   (available commands). This is where you learn whether a built-in eval exists or the harness is needed.
5. **Round-trip, per feature, one at a time:**
   - code -> scene: write or modify a builder `.cs` for the feature, let the plugin's auto-sync apply it
     (M-Auto file watcher), assert the live scene reflects it (read scene state via eval or a harness
     command).
   - scene -> code: apply a known scene edit (via eval or a harness command), let the plugin sync back,
     assert the builder `.cs` updated correctly.
   - console: read the editor console and assert no errors or unexpected warnings across both directions.
6. **Report** structured results per feature: code->scene pass or fail, scene->code pass or fail, console
   clean or not, with the concrete mismatch on any failure.
7. **Teardown:** stop the editor daemon (its process), and if you installed the pipeline package into a
   shared project, restore its manifest and `packages-lock.json` and remove any stale `Temp/UnityLockfile`.

## Constraints

- Do not run while you (or anyone) have a Unity editor open. Single-instance per project path, and a
  personal license may allow only one editor seat, so a second editor can fail to launch. Whether a
  personal license permits the automated editor while your working editor is closed is still unverified;
  treat "close your editor first" as required.
- Beta flakiness to expect and handle: Play Mode domain reloads regenerate the pipeline bearer token
  (reconnect on 401), modals block without `-automated`, and asset refresh sometimes needs editor
  foreground focus.
- Everything here is grounded truth or it did not happen. A live pass claim must quote the actual
  round-trip assertion and the console read. If the editor never connected, say so; do not infer a pass.

## Project setup (dedicated live-verify project)

A minimal Unity 6 project whose `Packages/manifest.json` references
`"com.codescenes": "file:/home/paul/Source/CodeScenesUnity/com.codescenes"` (loads exactly the built
package), plus the builders and scenes that exercise the milestone under test. Keep it separate from
`unity-gate` and from the interactive manual test project at `../Unity/SceneBuilderTest`. Left unbuilt for
now; the manual test project is the closest existing reference for how the `file:` package ref, the
out-of-`Assets/` builder layout, and the auto toggle are wired.
