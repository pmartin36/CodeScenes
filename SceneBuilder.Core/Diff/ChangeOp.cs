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

    // M3 component ChangeOps (b4-t1). LogicalId (base) is the owning GameObject's LogicalId.
    public sealed record AddComponent : ChangeOp
    {
        public ComponentData Component { get; init; } = new();
    }

    public sealed record RemoveComponent : ChangeOp
    {
        public string ComponentLogicalId { get; init; } = "";
        public TypeRef ComponentType { get; init; } = new TypeRef("");
    }

    public sealed record SetField : ChangeOp
    {
        public string ComponentLogicalId { get; init; } = "";
        public string Path { get; init; } = "";
        public ValueNode Value { get; init; } = ValueNode.Primitive.Int(0);
    }

    public sealed record ReorderComponent : ChangeOp
    {
        public string ComponentLogicalId { get; init; } = "";
        public int ToIndex { get; init; }
    }
}
