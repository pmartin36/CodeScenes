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

        public bool IsManaged(string globalObjectId)
        {
            if (string.IsNullOrEmpty(globalObjectId))
            {
                return false;
            }

            foreach (var entry in Entries)
            {
                if (entry.GlobalObjectId == globalObjectId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
