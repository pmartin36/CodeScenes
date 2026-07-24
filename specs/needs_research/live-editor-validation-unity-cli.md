# needs_research — Live-editor round-trip validation via the Unity CLI / Pipeline package

**Status:** research stub, not a build milestone. Promote to a milestone (Mn) — or, more likely, to a
reusable **end-of-milestone validation step** — once the open questions below are answered and the
underlying Unity tooling is out of beta enough to trust.

## Problem — the gap this closes
The product IS seamless, non-user-driven live sync (edit the scene → code updates; edit the code →
scene updates), yet the gate never exercises that *live* loop. `./verify.sh` Layer 2 drives real Unity,
but through **batchmode EditMode tests that call `SceneBuilderBuild.Run` / `SceneBuilderSync` directly**.
That is the real Unity boundary (real `GameObject`/`SerializedProperty`/`GlobalObjectId`/`AssetDatabase`),
but it is NOT the running editor Paul actually opens: no file-watcher fire, no real domain reload, no
`ObjectChangeEvents` timing, no plugin auto-sync driving itself. So "it's green" can still mean "surprises
when I open the editor." That residual gap is the single highest-value confidence hole we have, precisely
because live sync is the whole value prop.

The idea (Paul, 2026-07-23): append a **live round-trip validation step to the end of a milestone's
gate** — drive a *running* editor from outside, make an edit, confirm it round-trips, and confirm the
console is clean. Concretely:
- Edit the scene via the CLI → assert `scene.cs` updated (scene→code).
- Edit `scene.cs` → assert the live scene updated (code→scene).
- Assert **zero console errors/warnings** across both.

## The candidate tooling — Unity Pipeline package + `unity` CLI
`com.unity.pipeline` (Unity 6.0+) stands up a **local HTTP server inside a RUNNING editor**; the `unity`
CLI talks to it. Relevant surface:
- **`[CliCommand]` / `[CliArg]`** attributes expose project C# static methods as agent-callable tools;
  `unity command` lists what a connected editor exposes. We'd register a small harness in
  `com.codescenes/` (apply-scene-edit, read-builder-source, force-sync, dump-console).
- **`unity eval`** runs C# live in the editor with no domain reload — cheap pokes/asserts.
- **`unity test`** runs Edit/Play tests (NUnit XML; exit code 6 = tests failed) — structured, agent-readable.
- Structured JSON/TSV output + clear exit codes; console logs readable.
- Docs: docs.unity.com/en-us/unity-production-pipeline/local-tools-cli. See memory
  `unity-cli-pipeline-live-validation` for the full evaluation.

## Why it's hard (and parked)
Do NOT bake this into the hard per-task `./verify.sh` gate as-is — the gate must stay deterministic,
offline, fast, and self-contained. The candidate tool currently violates all four:
- **0.1.0 beta, shifting command names.** The gate is the one thing that must never lie; building it on
  an unstable beta surface is backwards. Reviewer guidance: "wait for beta stabilization."
- **Mandatory `unity auth login`** (service-account / cloud auth). Our gate today runs on a locally
  activated license with zero network per run; adding a cloud handshake (and its expiry failure mode)
  regresses reliability.
- **~16× latency (~0.8s/call) + flakiness:** PlayMode domain-reload regenerates bearer tokens → 401s
  mid-session; modal dialogs block and need `-automated`; foreground focus sometimes required.
- **Drives a RUNNING editor**, fighting our batchmode single-instance model — we'd hold a live editor
  daemon open (the thing CLAUDE.md says not to do during a gate) and manage its lifecycle.

So the shape is an **opt-in `verify-live.sh`** run at milestone boundaries (not per-task), kept OUT of
the hard gate until the tooling stabilizes — then promote pieces in.

## Open questions to resolve before promotion
1. **Auth offline:** can a service account + floating/offline license make `verify-live.sh` run with no
   per-run network, on a fresh machine, non-interactively? If not, it stays a manual/local affordance.
2. **Daemon lifecycle:** how do we hold one persistent automated editor open (`-automated`), detect it's
   ready, recover from the PlayMode-token 401 and from a domain reload mid-sequence, and not collide with
   the batchmode gate's single-instance lock (separate project path? serialized ordering?).
3. **The `[CliCommand]` harness surface:** minimal set of commands in `com.codescenes/` to (a) apply a
   known scene edit, (b) read the current builder `.cs`, (c) force a sync in each direction, (d) dump
   console errors/warnings since a marker. Kept editor-only, outside `Assets/` compile pressure.
4. **Determinism:** is a live round-trip stable enough to assert byte-equality on `scene.cs`, or only
   structural/semantic equality (timing/ordering nondeterminism)? What's the flake budget for a
   milestone-boundary check vs a hard gate?
5. **Latency envelope:** is a full live round-trip fast enough to run per-milestone (minutes ok), and is
   any subset ever cheap/stable enough to earn its way into the per-task gate later?
6. **Bonus scope:** the same harness could let an agent drive Paul's live manual-test project
   ([manual-test-project], `../Unity/SceneBuilderTest`) to validate the Crossy Road demo
   ([demo-scene-crossy-road]) — is that a second promotion target?

## Related
- Overlaps [live-continuous-sync.md] (the M-Auto live-sync driver) — this validates that loop rather
  than building it.
- Depends on M-Auto's file-watcher + auto-sync path existing to have a live loop worth validating.
- Sits naturally as a reusable **post-milestone gate step**, not a one-off feature — promotion should
  define it as pipeline infrastructure (a `verify-live.sh` companion to `verify.sh`), not a single Mn.
