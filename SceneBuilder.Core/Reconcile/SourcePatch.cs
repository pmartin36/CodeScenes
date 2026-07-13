namespace SceneBuilder.Core.Reconcile
{
    public sealed record SourcePatch
    {
        public string FilePath { get; init; } = "";
        public SourceEdit[] Edits { get; init; } = System.Array.Empty<SourceEdit>();
    }
}
