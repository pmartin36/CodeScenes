namespace SceneBuilder.Core.Reconcile
{
    public abstract record SourceEdit
    {
        public string Anchor { get; init; } = "";
    }

    public sealed record PatchArgument : SourceEdit
    {
        public string ArgName { get; init; } = "";
        public string NewExpr { get; init; } = "";
    }

    public sealed record MoveStatement : SourceEdit
    {
        public string? NewParentAnchor { get; init; }
    }

    public sealed record ReorderStatement : SourceEdit
    {
        public int NewSiblingIndex { get; init; }
    }
}
