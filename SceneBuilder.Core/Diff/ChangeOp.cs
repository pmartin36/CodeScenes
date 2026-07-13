using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Diff
{
    public abstract record ChangeOp
    {
        public string LogicalId { get; init; } = "";
    }

    public sealed record AddNode : ChangeOp
    {
        public string Name { get; init; } = "";
        public string? ParentLogicalId { get; init; }
    }

    public sealed record RemoveNode : ChangeOp;

    public sealed record Reparent : ChangeOp
    {
        public string? NewParentLogicalId { get; init; }
    }

    public sealed record Reorder : ChangeOp
    {
        public int SiblingIndex { get; init; }
    }

    public sealed record SetName : ChangeOp
    {
        public string Name { get; init; } = "";
    }

    public sealed record SetTag : ChangeOp
    {
        public string Tag { get; init; } = "Untagged";
    }

    public sealed record SetLayer : ChangeOp
    {
        public int Layer { get; init; }
    }

    public sealed record SetActive : ChangeOp
    {
        public bool Active { get; init; } = true;
    }

    public sealed record SetStatic : ChangeOp
    {
        public bool IsStatic { get; init; }
    }

    public sealed record SetTransform : ChangeOp
    {
        public TransformData Transform { get; init; } = new();
    }
}
