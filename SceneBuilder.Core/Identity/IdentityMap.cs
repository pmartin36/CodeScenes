using System;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Identity
{
    public record IdentityMap
    {
        [JsonPropertyOrder(0)]
        public int SchemaVersion { get; init; }

        [JsonPropertyOrder(1)]
        public string Scene { get; init; } = "";

        [JsonPropertyOrder(2)]
        public IdentityMapEntry[] Entries { get; init; } = Array.Empty<IdentityMapEntry>();

        [JsonPropertyOrder(3)]
        public AssetEntry[] Assets { get; init; } = Array.Empty<AssetEntry>();
    }
}
