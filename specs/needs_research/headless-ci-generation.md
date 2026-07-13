# needs_research — Headless / CI scene generation

**Status:** research stub, not a build milestone. v1 assumes an interactive in-editor plugin.

## Problem
Generating or validating scenes from builder files without a human-driven Editor session — e.g. a CI
job that materializes scenes from code, or validates that committed builder files still produce their
scenes, or regenerates on a build server.

## What already helps
- The `SceneBuilder.Core` layer is Unity-free and already runs headless (parse, diff, canonical
  serialize, codegen, Roslyn patch) — CI can run all of that today via `dotnet test`/CLI.
- The gap is the **Editor adapter** half (Materialize execution, snapshot reading), which needs a
  Unity process.

## Open questions to resolve before promotion
1. Run the adapter under `unity -batchmode -executeMethod` (per research: watch the filename≠classname
   footgun, `-nographics` GPU-op limits) — is that reliable enough for CI?
2. What does "validate a builder file in CI" assert — that Materialize produces a no-op Plan against
   the committed scene (i.e. code and scene agree)? That's a strong drift-detection CI check.
3. Licensing/runner cost of Unity in CI.
4. Can any of it avoid Unity entirely by emitting `.unity` YAML directly (revisits the parked
   direct-YAML path and its fragility)?

## Related
The "no-op Plan = code and scene agree" idea is a natural CI gate built on §5. Revisit after M2–M7.
