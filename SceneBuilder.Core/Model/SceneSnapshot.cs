using System;

namespace SceneBuilder.Core.Model
{
    public record SceneSnapshot
    {
        public int SchemaVersion { get; init; }

        public SnapshotNode[] Roots { get; init; } = Array.Empty<SnapshotNode>();
    }
}
