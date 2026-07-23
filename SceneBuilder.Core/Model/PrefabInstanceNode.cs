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
        public AddedGameObject[] AddedGameObjects { get; init; } = Array.Empty<AddedGameObject>();

        [JsonPropertyOrder(15)]
        public OverrideTarget[] RemovedGameObjects { get; init; } = Array.Empty<OverrideTarget>();

        // b4-t2: `.On(selector, ...)` resolved targets. Nullable + omit-when-null so instances
        // without `.On` serialize byte-identically to before this task.
        [JsonPropertyOrder(16)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ScopedOverride[]? ScopedOverrides { get; init; }
    }
}
