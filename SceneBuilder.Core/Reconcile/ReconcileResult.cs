namespace SceneBuilder.Core.Reconcile
{
    public sealed record ReconcileResult
    {
        public SourcePatch Patch { get; init; } = new();
        public Conflict[] Conflicts { get; init; } = System.Array.Empty<Conflict>();
    }
}
