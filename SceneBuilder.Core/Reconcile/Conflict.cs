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
        UnresolvedNestedSelector,

        // m-ui-recttransform b4-t1: a matched node's RectTransform layout differs but the node's
        // authoring construct cannot host a `.RectTransform(...)` call (a PrefabInstanceNode root --
        // InstanceHandle has no such member, see InstanceHandle.cs). Emitting the edit would produce
        // source that does not compile, so it is surfaced as a located Conflict instead and NO edit
        // is emitted.
        UnlocalizableRectTransform,

        // A member of a nested struct/class field has no compiling emission form (an asset or
        // scene-object reference held inside the struct, or a serialized member with no public
        // spelling) and is at a NON-default value. Emitting it would produce source that does not
        // compile, so the member is excluded from the initializer and reported here instead. Never
        // raised for a member AT its type default (nothing was excluded that a user would notice).
        UnrepresentableValue,
    }

    public sealed record Conflict
    {
        private readonly ConflictKind _kind;
        private readonly string _reason = "";

        // Records lose the implicit parameterless constructor once any constructor is declared --
        // kept explicitly so a plain object initializer (a non-guarded kind) still compiles, e.g.
        // com.codescenes/Editor/NestedOverrideBootstrap.cs's adapter-side construction.
        public Conflict() { }

        // The ONE door that can set a guarded kind, used only by FromReport. Bypasses the Kind
        // init guard by writing the backing field directly.
        private Conflict(ConflictKind kind) { _kind = kind; }

        public string? LogicalId { get; init; }
        public string? GlobalObjectId { get; init; }

        public ConflictKind Kind
        {
            get => _kind;
            init => _kind = value != ConflictKind.UnrepresentableValue
                ? value
                : throw new System.ArgumentException(
                    "An UnrepresentableValue report is constructed through Conflict.FromReport(...), " +
                    "which requires the object anchor, the component type full name and the " +
                    "field/member key. Set Located, not Kind.");
        }

        public string Reason
        {
            get => Located?.Compose() ?? _reason;
            init => _reason = value;
        }

        public SourceSpan? Location { get; init; }

        // Identity of the STANDING condition this report describes -- a fact of the scene+source
        // that recurs on every reconcile until the user changes one of them, not an event of this
        // pass. Two reports with the same key are the same report: Reconciler routes them to
        // ReconcileResult.Notes and a surfacing channel shows one of them, once per editor session.
        // Null = an event, surfaced every time it happens.
        public string? RecurrenceKey { get; init; }

        // The located-report data (anchor, component type full name, field/member key) an
        // UnrepresentableValue conflict must carry. FromReport is the one construction door for a
        // conflict of that kind; every other kind leaves this null.
        public LocatedReport? Located { get; init; }

        public static Conflict FromReport(
            ConflictKind kind, LocatedReport report, string? recurrenceKey, SourceSpan? location) =>
            new Conflict(kind)
            {
                LogicalId = report.LogicalId,
                Located = report,
                RecurrenceKey = recurrenceKey,
                Location = location,
            };
    }
}
