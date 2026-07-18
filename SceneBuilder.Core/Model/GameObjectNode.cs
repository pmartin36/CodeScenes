using System;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    [JsonPolymorphic]
    [JsonDerivedType(typeof(PrefabInstanceNode), "PrefabInstance")]
    public record GameObjectNode
    {
        [JsonPropertyOrder(0)]
        public string LogicalId { get; init; } = "";

        [JsonPropertyOrder(1)]
        public string Name { get; init; } = "";

        [JsonPropertyOrder(2)]
        public string Tag { get; init; } = "Untagged";

        [JsonPropertyOrder(3)]
        public int Layer { get; init; } = 0;

        [JsonPropertyOrder(4)]
        public bool Active { get; init; } = true;

        [JsonPropertyOrder(5)]
        public bool IsStatic { get; init; } = false;

        [JsonPropertyOrder(6)]
        public TransformData Transform { get; init; } = new();

        [JsonPropertyOrder(7)]
        public ComponentData[] Components { get; init; } = Array.Empty<ComponentData>();

        [JsonPropertyOrder(8)]
        public GameObjectNode[] Children { get; init; } = Array.Empty<GameObjectNode>();
    }
}
