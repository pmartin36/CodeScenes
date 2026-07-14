using SceneBuilder.Core.Model;

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

    public sealed record AppendStatement : SourceEdit
    {
        // Predicted synthesized/handle LogicalId for the created object.
        // Equals the AddedEntry.LogicalId the Reconciler emits (b1-t2 / b2-t1),
        // and equals what BuilderParser assigns after the Applier rewrites source (b4).
        public string NewLogicalId { get; init; } = "";

        // Parent LogicalId to insert under; null => root append (receiver = scene param).
        public string? ParentAnchor { get; init; }

        public string Name { get; init; } = "";

        // Non-default GameObject data. null => omit from emitted source (keep it clean).
        public TransformData? Transform { get; init; }
        public bool? Active { get; init; }
        public string? Tag { get; init; }
        public int? Layer { get; init; }
        public bool? IsStatic { get; init; }

        // Variable name THIS statement declares (`var <Handle> = ...`) when it heads a
        // subtree whose descendants reference it (b2-t3). null => no declaration.
        // When set, Handle == NewLogicalId.
        public string? Handle { get; init; }

        // Receiver variable for a CHILD append (the parent's handle, existing or newly
        // introduced). null for a root append.
        public string? ParentHandle { get; init; }

        // true => the (currently handle-less) parent statement must be rewritten to declare
        // ParentHandle before the child is appended (b2-t2 reconcile / b3-t3 apply).
        public bool IntroduceParentHandle { get; init; }
    }

    public sealed record RemoveStatement : SourceEdit
    {
        // Uses inherited SourceEdit.Anchor = the LogicalId of the statement to delete.
    }

    public enum FlagKind { Tag, Layer, Active, Static }

    public sealed record PatchFlagArgument : SourceEdit
    {
        public FlagKind Flag { get; init; }
        public string NewExpr { get; init; } = "";
    }

    public sealed record IntroduceFlagCall : SourceEdit
    {
        public FlagKind Flag { get; init; }
        public string? ArgExpr { get; init; }
    }

    public sealed record RemoveFlagCall : SourceEdit
    {
        public FlagKind Flag { get; init; }
    }
}
