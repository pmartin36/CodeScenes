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

    // Emitted for an unmatched (new) PrefabInstanceNode instead of AddNode — parenting/sibling
    // index ride on this op (mirrors the InstantiatePrefab PlanOp), so no SetParent is synthesized
    // in Materializer's pass-B.
    public sealed record AddInstance : ChangeOp
    {
        public string Guid { get; init; } = "";
        public string? ParentLogicalId { get; init; }
        public int SiblingIndex { get; init; }
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

    // b2-t1: RectTransform layout diff, sibling of SetTransform. Transform carries the node's
    // DESIRED TransformData (all five UI fields); Changed names exactly the per-axis rect channels
    // that differ post driven-mask (never the whole-field alias, never a base-transform channel).
    public sealed record SetRectTransform : ChangeOp
    {
        public TransformData Transform { get; init; } = new();
        public ChannelMask Changed { get; init; }
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

    // Mirrors Plan.SetUnityEvent (PlanOps.cs): one op replaces the full ordered persistent-call
    // list at Path.
    public sealed record SetUnityEvent : ChangeOp
    {
        public string ComponentLogicalId { get; init; } = "";
        public string Path { get; init; } = "";
        public ValueNode.UnityEventListeners Listeners { get; init; }
            = new ValueNode.UnityEventListeners(System.Array.Empty<UnityEventListener>());
    }

    public sealed record ReorderComponent : ChangeOp
    {
        public string ComponentLogicalId { get; init; } = "";
        public int ToIndex { get; init; }
    }

    // b3-t2: instance-override ChangeOps. LogicalId (base) is the owning PrefabInstanceNode's
    // LogicalId; Target identifies the nested prefab member the override/added/removed-component
    // applies to (independent of the instance's own LogicalId). Names mirror the b3-t1 PlanOps
    // 1:1, per the existing Change.X -> Plan.X convention.
    public sealed record SetInstanceOverride : ChangeOp
    {
        public OverrideTarget Target { get; init; } = new();
        public string PropertyPath { get; init; } = "";
        public ValueNode Value { get; init; } = new ValueNode.Unsupported("");
        public ValueNode? ObjectReference { get; init; }
    }

    public sealed record AddInstanceComponent : ChangeOp
    {
        public OverrideTarget Target { get; init; } = new();
        public ComponentData Component { get; init; } = new();
    }

    public sealed record RemoveInstanceComponent : ChangeOp
    {
        public OverrideTarget Target { get; init; } = new();
    }

    public sealed record RevertInstanceOverride : ChangeOp
    {
        public OverrideTarget Target { get; init; } = new();
        public string PropertyPath { get; init; } = "";
    }

    public sealed record RevertAddedComponent : ChangeOp
    {
        public OverrideTarget Target { get; init; } = new();
        public string ComponentLogicalId { get; init; } = "";
    }

    public sealed record RevertRemovedComponent : ChangeOp
    {
        public OverrideTarget Target { get; init; } = new();
    }

    // b3-t1: added/removed child GameObject ChangeOps, mirroring the AddInstanceComponent/
    // RemoveInstanceComponent/RevertAddedComponent shape. LogicalId (base) is the owning
    // PrefabInstanceNode's LogicalId.
    public sealed record AddInstanceChild : ChangeOp
    {
        public OverrideTarget Target { get; init; } = new();
        public GameObjectNode Node { get; init; } = new();
    }

    public sealed record RemoveInstanceChild : ChangeOp
    {
        public OverrideTarget Target { get; init; } = new();
    }

    public sealed record RevertAddedChild : ChangeOp
    {
        public OverrideTarget Target { get; init; } = new();
        public string ChildLogicalId { get; init; } = "";
    }

    // b3-t2: emitted when a snapshot-only RemovedGameObjects entry is absent from desired — the
    // matching revert for the RemoveInstanceChild direction, mirroring RevertRemovedComponent.
    public sealed record RevertRemovedChild : ChangeOp
    {
        public OverrideTarget Target { get; init; } = new();
    }
}
