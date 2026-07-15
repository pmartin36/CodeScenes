using System.Text.Json.Serialization;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Plan
{
    public sealed record DestroyObject : PlanOp;

    public sealed record SetParent : PlanOp
    {
        [JsonPropertyOrder(1)]
        public string? ParentLogicalId { get; init; }
    }

    public sealed record ReorderChild : PlanOp
    {
        [JsonPropertyOrder(1)]
        public int SiblingIndex { get; init; }
    }

    public sealed record SetName : PlanOp
    {
        [JsonPropertyOrder(1)]
        public string Name { get; init; } = "";
    }

    public sealed record SetTag : PlanOp
    {
        [JsonPropertyOrder(1)]
        public string Tag { get; init; } = "Untagged";
    }

    public sealed record SetLayer : PlanOp
    {
        [JsonPropertyOrder(1)]
        public int Layer { get; init; }
    }

    public sealed record SetActive : PlanOp
    {
        [JsonPropertyOrder(1)]
        public bool Active { get; init; } = true;
    }

    public sealed record SetStatic : PlanOp
    {
        [JsonPropertyOrder(1)]
        public bool IsStatic { get; init; }
    }

    public sealed record SetField : PlanOp
    {
        [JsonPropertyOrder(1)]
        public string Path { get; init; } = "";

        [JsonPropertyOrder(2)]
        public ValueNode Value { get; init; } = ValueNode.Primitive.Int(0);
    }

    public sealed record AddComponent : PlanOp
    {
        [JsonPropertyOrder(1)]
        public TypeRef Type { get; init; } = new TypeRef("");
    }

    public sealed record RemoveComponent : PlanOp;

    public sealed record ReorderComponent : PlanOp
    {
        [JsonPropertyOrder(1)]
        public string GameObjectLogicalId { get; init; } = "";

        [JsonPropertyOrder(2)]
        public string ComponentLogicalId { get; init; } = "";

        [JsonPropertyOrder(3)]
        public int ToIndex { get; init; }
    }

    public sealed record SetAssetRef : PlanOp
    {
        [JsonPropertyOrder(1)]
        public string Path { get; init; } = "";

        // null/empty => None/clear form
        [JsonPropertyOrder(2)]
        public string? Guid { get; init; }

        // 0 = main asset
        [JsonPropertyOrder(3)]
        public long FileId { get; init; }
    }
}
