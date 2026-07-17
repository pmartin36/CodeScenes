namespace SceneBuilder.Core.Reconcile
{
    public enum ConflictKind
    {
        AmbiguousAnchor,
        MissingSourceAnchor,
        ReferencedHandle,
        DuplicateLogicalId,
        // A source handle's target vanished from the scene (Detection 1), or a snapshot target
        // resolves to nothing live (Detection 2) — see ComponentReconciler's FIELD-VALUE DIFF pass.
        DanglingReference
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
