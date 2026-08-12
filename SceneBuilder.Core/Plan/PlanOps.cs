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

    // Mirrors Change.SetUnityEvent (ChangeOp.cs): one op replaces the full ordered persistent-call
    // list at Path.
    public sealed record SetUnityEvent : PlanOp
    {
        [JsonPropertyOrder(1)]
        public string Path { get; init; } = "";

        [JsonPropertyOrder(2)]
        public ValueNode.UnityEventListeners Listeners { get; init; }
            = new ValueNode.UnityEventListeners(System.Array.Empty<UnityEventListener>());
    }

    // Mirrors Change.SetManagedReference (ChangeOp.cs): one op replaces the whole
    // [SerializeReference] instance at Path.
    public sealed record SetManagedReference : PlanOp
    {
        [JsonPropertyOrder(1)]
        public string Path { get; init; } = "";

        [JsonPropertyOrder(2)]
        public TypeRef? ConcreteType { get; init; }

        [JsonPropertyOrder(3)]
        public FieldMap Fields { get; init; } = FieldMap.Empty;
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

    public sealed record SetReference : PlanOp
    {
        [JsonPropertyOrder(1)]
        public string Path { get; init; } = "";

        // null/empty => clear-slot form
        [JsonPropertyOrder(2)]
        public string? TargetLogicalId { get; init; }
    }

    // Carries the AUTHORED length of a reference list (ObjectRef/AssetRef elements), so the live
    // array can shrink -- per-index SetReference/SetAssetRef ops alone can only grow it.
    public sealed record SetArraySize : PlanOp
    {
        [JsonPropertyOrder(1)]
        public string Path { get; init; } = "";

        [JsonPropertyOrder(2)]
        public int Size { get; init; }
    }

    public sealed record InstantiatePrefab : PlanOp
    {
        [JsonPropertyOrder(1)]
        public string Guid { get; init; } = "";

        [JsonPropertyOrder(2)]
        public string? ParentLogicalId { get; init; }

        [JsonPropertyOrder(3)]
        public int SiblingIndex { get; init; }
    }

    public sealed record SetInstanceOverride : PlanOp
    {
        [JsonPropertyOrder(1)]
        public OverrideTarget Target { get; init; } = new();

        [JsonPropertyOrder(2)]
        public string PropertyPath { get; init; } = "";

        [JsonPropertyOrder(3)]
        public ValueNode Value { get; init; } = new ValueNode.Unsupported("");

        [JsonPropertyOrder(4)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ValueNode? ObjectReference { get; init; }
    }

    public sealed record AddInstanceComponent : PlanOp
    {
        [JsonPropertyOrder(1)]
        public OverrideTarget Target { get; init; } = new();

        [JsonPropertyOrder(2)]
        public ComponentData Component { get; init; } = new();
    }

    public sealed record RemoveInstanceComponent : PlanOp
    {
        [JsonPropertyOrder(1)]
        public OverrideTarget Target { get; init; } = new();
    }

    public sealed record RevertInstanceOverride : PlanOp
    {
        [JsonPropertyOrder(1)]
        public OverrideTarget Target { get; init; } = new();

        [JsonPropertyOrder(2)]
        public string PropertyPath { get; init; } = "";
    }

    public sealed record RevertAddedComponent : PlanOp
    {
        [JsonPropertyOrder(1)]
        public OverrideTarget Target { get; init; } = new();

        [JsonPropertyOrder(2)]
        public string ComponentLogicalId { get; init; } = "";
    }

    public sealed record RevertRemovedComponent : PlanOp
    {
        [JsonPropertyOrder(1)]
        public OverrideTarget Target { get; init; } = new();
    }

    // b3-t1: added/removed child GameObject PlanOps, mirroring the AddInstanceComponent/
    // RemoveInstanceComponent/RevertAddedComponent shape.
    public sealed record AddInstanceChild : PlanOp
    {
        [JsonPropertyOrder(1)]
        public OverrideTarget Target { get; init; } = new();

        [JsonPropertyOrder(2)]
        public GameObjectNode Node { get; init; } = new();
    }

    public sealed record RemoveInstanceChild : PlanOp
    {
        [JsonPropertyOrder(1)]
        public OverrideTarget Target { get; init; } = new();
    }

    public sealed record RevertAddedChild : PlanOp
    {
        [JsonPropertyOrder(1)]
        public OverrideTarget Target { get; init; } = new();

        [JsonPropertyOrder(2)]
        public string ChildLogicalId { get; init; } = "";
    }

    // b3-t2: mirrors RevertRemovedComponent.
    public sealed record RevertRemovedChild : PlanOp
    {
        [JsonPropertyOrder(1)]
        public OverrideTarget Target { get; init; } = new();
    }
}
