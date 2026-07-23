using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public record OverrideTarget
    {
        [JsonPropertyOrder(0)]
        public PrefabInstanceKey SubKey { get; init; } = new();

        [JsonPropertyOrder(1)]
        public string ComponentType { get; init; } = "";

        [JsonPropertyOrder(2)]
        public string ChildPath { get; init; } = "";

        public virtual bool Equals(OverrideTarget? other) =>
            other is not null
            && EqualityComparer<PrefabInstanceKey>.Default.Equals(SubKey, other.SubKey)
            && ComponentType == other.ComponentType;

        public override int GetHashCode() => System.HashCode.Combine(SubKey, ComponentType);
    }
}
