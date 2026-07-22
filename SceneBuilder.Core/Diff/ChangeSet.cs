namespace SceneBuilder.Core.Diff
{
    public record ChangeSet
    {
        public ChangeOp[] Ops { get; init; } = System.Array.Empty<ChangeOp>();

        // b3-t2: informational diagnostics (e.g. "prefab overrides preserved, not modelled") that
        // ride out of the Differ alongside Ops but are NOT ops themselves — never diffed/applied.
        // Population (the actual override-detection) is the code-writer's job; this is the data
        // carrier stub only.
        public System.Collections.Generic.IReadOnlyList<SceneBuilder.Core.Validation.Diagnostic> Diagnostics { get; init; }
            = System.Array.Empty<SceneBuilder.Core.Validation.Diagnostic>();

        // b3-t3: located StaleOverride conflicts (and future conflict kinds) that ride out of the
        // Differ alongside Ops/Diagnostics. Population (InstanceOverrideDiff.DetectStaleOverrides +
        // wiring into WalkDesired) is the code-writer's job; this is the data carrier stub only —
        // mirrors Diagnostics exactly.
        public System.Collections.Generic.IReadOnlyList<SceneBuilder.Core.Reconcile.Conflict> Conflicts { get; init; }
            = System.Array.Empty<SceneBuilder.Core.Reconcile.Conflict>();
    }
}
