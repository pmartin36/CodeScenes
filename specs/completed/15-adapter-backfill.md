# Adapter Backfill — Unity Editor adapters (M1b / M2b / M2c / M3)

**Status: COMPLETE (2026-07-16).** The Unity Editor adapter backfill for these milestones is shipped:
the `com.codescenes/Editor/` adapter exercises the Core capabilities in the live editor — non-destructive
in-place Build, structural sync-back, flags sync-back, and the component/serialized-field layer.

Shipped adapter code lives in `com.codescenes/Editor/`, including `SerializedFieldBridge.cs`,
`AssetReferenceResolver.cs`, and `ComponentTypeResolver.cs`, alongside the build/sync seams.

The original planning guidance in this spec is **superseded** — do not follow it:
- The gate is `./verify.sh` (Core `dotnet build`/`dotnet test` **plus** the mandatory Unity EditMode layer),
  not a compile-only check.
- All feature and adapter work goes through the tdd-pipeline.
- Runtime behavior is covered by EditMode tests in `unity-gate/Assets/GateTests/` — it is not a manual user step.

See `CLAUDE.md` for the current operating contract.
