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

        // b4-t1: sidecar Assets[] channel — every populated AssetRef flowing from a snapshot
        // value into an emitted source edit (patch/introduce/append) is harvested here so the
        // adapter can persist it into IdentityMap.Assets. Cleared fields (AssetRef(null))
        // contribute no entry. Deduped by Guid (first occurrence wins).
        public AssetEntry[] AddedAssets { get; init; } = System.Array.Empty<AssetEntry>();

        // b2-t2: Unsupported snapshot component-field values — no edit is emitted for them
        // (source token untouched), surfaced here for the Reconcile patch preview (spec test 13:
        // flagged in BOTH previews). REUSES Plan.SkippedField (same shape). Populated by
        // Reconciler.Reconcile from ComponentReconciler's Unsupported-skip pass.
        public SkippedField[] Skipped { get; init; } = System.Array.Empty<SkippedField>();
    }
}
