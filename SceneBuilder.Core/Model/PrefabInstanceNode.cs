using System;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public record PrefabInstanceNode : GameObjectNode
    {
        [JsonPropertyOrder(9)]
        public AssetRef SourcePrefab { get; init; } = new();

        [JsonPropertyOrder(10)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ValueNode.Unsupported? OpaqueOverrides { get; init; }

        [JsonPropertyOrder(11)]
        public PropertyOverride[] Overrides { get; init; } = Array.Empty<PropertyOverride>();

        [JsonPropertyOrder(12)]
        public AddedComponent[] AddedComponents { get; init; } = Array.Empty<AddedComponent>();

        [JsonPropertyOrder(13)]
        public OverrideTarget[] RemovedComponents { get; init; } = Array.Empty<OverrideTarget>();

        [JsonPropertyOrder(14)]
        public GameObjectNode[] AddedGameObjects { get; init; } = Array.Empty<GameObjectNode>();

        [JsonPropertyOrder(15)]
        public OverrideTarget[] RemovedGameObjects { get; init; } = Array.Empty<OverrideTarget>();
    }
}
