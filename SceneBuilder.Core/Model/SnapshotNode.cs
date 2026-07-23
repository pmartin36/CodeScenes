using System;

namespace SceneBuilder.Core.Model
{
    public record SnapshotNode
    {
        public string GlobalObjectId { get; init; } = "";

        public string Name { get; init; } = "";

        public string Tag { get; init; } = "Untagged";

        public int Layer { get; init; } = 0;

        public bool Active { get; init; } = true;

        public bool IsStatic { get; init; } = false;

        public TransformData Transform { get; init; } = new();

        public ComponentData[] Components { get; init; } = Array.Empty<ComponentData>();

        public SnapshotNode[] Children { get; init; } = Array.Empty<SnapshotNode>();

        public string? SourcePrefabGuid { get; init; } = null;

        public PrefabInstanceKey? PrefabKey { get; init; } = null;

        public ValueNode.Unsupported? OpaqueOverrides { get; init; } = null;

        public PropertyOverride[] Overrides { get; init; } = Array.Empty<PropertyOverride>();

        public AddedComponent[] AddedComponents { get; init; } = Array.Empty<AddedComponent>();

        public OverrideTarget[] RemovedComponents { get; init; } = Array.Empty<OverrideTarget>();

        // b3-t2: live-instance authority for child-GO diff (AddedGameObjects/RemovedGameObjects).
        // Populated by the adapter READ side (b5-t2); here so the headless diff can compile/exercise
        // fixture-built snapshots.
        public AddedGameObject[] AddedGameObjects { get; init; } = Array.Empty<AddedGameObject>();

        public OverrideTarget[] RemovedGameObjects { get; init; } = Array.Empty<OverrideTarget>();
    }
}
