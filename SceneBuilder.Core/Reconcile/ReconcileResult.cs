using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Plan;

namespace SceneBuilder.Core.Reconcile
{
    public sealed record ReconcileResult
    {
        public SourcePatch Patch { get; init; } = new();
        public Conflict[] Conflicts { get; init; } = System.Array.Empty<Conflict>();
        public IdentityMapEntry[] AddedEntries { get; init; } = System.Array.Empty<IdentityMapEntry>();
        public string[] RemovedLogicalIds { get; init; } = System.Array.Empty<string>();

        // b2-t2: Unsupported snapshot component-field values — no edit is emitted for them
        // (source token untouched), surfaced here for the Reconcile patch preview (spec test 13:
        // flagged in BOTH previews). REUSES Plan.SkippedField (same shape). Populated by
        // Reconciler.Reconcile from ComponentReconciler's Unsupported-skip pass.
        public SkippedField[] Skipped { get; init; } = System.Array.Empty<SkippedField>();
    }
}
