using System;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public record ProjectCatalogManifest
    {
        [JsonPropertyOrder(0)]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyOrder(1)]
        public string[] Tags { get; init; } = Array.Empty<string>();

        [JsonPropertyOrder(2)]
        public LayerEntry[] Layers { get; init; } = Array.Empty<LayerEntry>();
    }

    public record LayerEntry
    {
        [JsonPropertyOrder(0)]
        public int Index { get; init; }

        [JsonPropertyOrder(1)]
        public string Name { get; init; } = "";
    }
}
