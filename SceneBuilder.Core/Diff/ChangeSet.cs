namespace SceneBuilder.Core.Diff
{
    public record ChangeSet
    {
        public ChangeOp[] Ops { get; init; } = System.Array.Empty<ChangeOp>();
    }
}
