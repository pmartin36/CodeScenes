# SceneBuilder

**Code-native Unity scene authoring with bidirectional code↔scene sync.**

Define a Unity scene as a flat C# builder file; on **Build** it materializes into a real Unity scene,
and edits you make in the Unity editor **sync back** into the builder code. Built for LLM-driven
workflows: an LLM authors and maintains scenes as code — its native substrate, a whole scene in
context — while humans keep editing visually in the editor, and the two stay in agreement.

> The GitHub repo is named `UnitySceneManager`; the product/plugin is **SceneBuilder**.

## Why

- **Wedge — code-native authoring.** Existing Unity AI tools drive the editor via one tool-call per
  action. SceneBuilder makes the scene a single coherent code artifact the model can read, generate,
  and validate against a schema — playing to the LLM's strengths instead of fighting them.
- **Moat — the sync layer.** Construction from code is commodity (Unity's Editor APIs already do it).
  The value is the durable, *living* code↔scene relationship: a stable identity map plus a
  reconciliation engine that keeps both sides honest. Nobody has shipped this for Unity.

## Architecture

Split along the testability seam:

- **`SceneBuilder.Core`** — Unity-free .NET (`netstandard2.1`). Parses builder files (Roslyn), diffs,
  produces materialize Plans (code→scene) and source patches (scene→code), owns the canonical
  serializer and the identity map. **Fully unit-tested headless** via `dotnet test` — no mocks.
- **`SceneBuilder.Editor` / `.Authoring`** (Unity 6) — the thin in-editor adapter (executes Plans,
  reads scene snapshots, captures `ObjectChangeEvents`) and the fluent builder API. Consumed by a Unity
  project as a local UPM package.

Correctness backbone: state reconciliation keyed on Unity's durable `GlobalObjectId`; the editor event
stream is only a trigger. Builds reconcile **in place** (never wipe-and-recreate) to preserve identity.

## Status — work in progress

- **Specs:** complete — `specs/00-foundation.md` is the authoritative contract; milestones **M0–M11**
  plus `specs/needs_research/`.
- **Core:** M0 (skeleton & harness) done; **M1** (hierarchy + transforms) and **M2** (sync-back) in
  progress, built test-first behind a `dotnet test` gate.
- **Unity adapter:** not started — built after the Core M0–M2 API is green.

## Build & test the Core

```sh
# dotnet 8 SDK required (this machine has it at ~/.dotnet)
export PATH="$HOME/.dotnet:$PATH"
dotnet test SceneBuilder.sln
```

## Layout

```
SceneBuilder.Core/         # Unity-free brains (parse / diff / plan / reconcile / identity)
SceneBuilder.Core.Tests/   # xUnit — real behavior tests, headless
SceneBuilder.sln
specs/
  00-foundation.md         # THE CONTRACT — read this first
  01-m0 … 12-m11           # milestone specs
  needs_research/          # not-yet-spec-ready problems
  completed/               # milestones move here once green + confirmed in Unity
```

The Unity test project that consumes this plugin lives separately and references it via a local UPM
`file:` path.

## Specs

Start with **`specs/00-foundation.md`**, then `specs/01-m0-skeleton.md` onward.
