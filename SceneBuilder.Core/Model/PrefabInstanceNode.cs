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
    }
}
