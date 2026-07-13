namespace SceneBuilder.Core.Reconcile
{
    public enum ConflictKind
    {
        AmbiguousAnchor,
        MissingSourceAnchor
    }

    public sealed record Conflict
    {
        public string? LogicalId { get; init; }
        public string? GlobalObjectId { get; init; }
        public ConflictKind Kind { get; init; }
        public string Reason { get; init; } = "";
        public SourceSpan? Location { get; init; }
    }
}
