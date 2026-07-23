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
        DanglingReference,

        // b3-t3: the source prefab's default for an overridden property drifted (the override's
        // recorded BaseValue no longer matches the snapshot's current BaseValue) AND the live
        // instance value now equals the NEW default — ambiguous whether the user wants to keep the
        // override or adopt the new default. Neither silently kept nor dropped (spec #9).
        StaleOverride,

        // b4-t1: Instance(Prefabs.X) where X is absent from the FacadeCatalog (or the catalog
        // is null). Located over the `Prefabs.X` argument span. Never a throw, never a silent
        // drop — the instance node is still produced.
        UnknownFacadeReference,

        // b7-t2: a below-root override/added-component/removed-component/removed-child Target (or
        // AddedGameObject.Parent) whose ChildPath resolves to NO live sub-object under a LIVE prefab
        // instance root (see NestedOverrideBootstrap). Never a silent drop.
        UnresolvedNestedSelector
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
