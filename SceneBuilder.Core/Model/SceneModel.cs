using System;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public record SceneModel
    {
        [JsonPropertyOrder(0)]
        public int SchemaVersion { get; init; }

        [JsonPropertyOrder(1)]
        public GameObjectNode[] Roots { get; init; } = Array.Empty<GameObjectNode>();
    }
}
