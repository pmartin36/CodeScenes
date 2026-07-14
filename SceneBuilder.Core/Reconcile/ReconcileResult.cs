using SceneBuilder.Core.Identity;

namespace SceneBuilder.Core.Reconcile
{
    public sealed record ReconcileResult
    {
        public SourcePatch Patch { get; init; } = new();
        public Conflict[] Conflicts { get; init; } = System.Array.Empty<Conflict>();
        public IdentityMapEntry[] AddedEntries { get; init; } = System.Array.Empty<IdentityMapEntry>();
        public string[] RemovedLogicalIds { get; init; } = System.Array.Empty<string>();
    }
}
