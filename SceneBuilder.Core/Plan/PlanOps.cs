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
        public FieldValue Value { get; init; } = new Vec3Value();
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(Vec3Value), "Vec3")]
    [JsonDerivedType(typeof(QuatValue), "Quat")]
    public abstract record FieldValue;

    public sealed record Vec3Value : FieldValue
    {
        public Vec3 Value { get; init; }
    }

    public sealed record QuatValue : FieldValue
    {
        public Quat Value { get; init; }
    }
}
