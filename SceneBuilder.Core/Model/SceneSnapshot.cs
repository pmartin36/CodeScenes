using System;

namespace SceneBuilder.Core.Model
{
    public record SceneSnapshot
    {
        public int SchemaVersion { get; init; }

        public SnapshotNode[] Roots { get; init; } = Array.Empty<SnapshotNode>();

        // Per-TYPE default field templates for the component types present in this snapshot.
        // Type = the component type (TypeRef, keyed by FullName), Fields = every serialized field
        // at a freshly-constructed instance's value, LogicalId unused ("").
        public ComponentData[] ComponentDefaults { get; init; } = Array.Empty<ComponentData>();
    }
}
