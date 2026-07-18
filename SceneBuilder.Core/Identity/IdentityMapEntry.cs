using System.Text.Json.Serialization;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Identity
{
    public record IdentityMapEntry
    {
        [JsonPropertyOrder(0)]
        public string LogicalId { get; init; } = "";

        [JsonPropertyOrder(1)]
        public string GlobalObjectId { get; init; } = "";

        [JsonPropertyOrder(2)]
        public string Kind { get; init; } = "GameObject";

        [JsonPropertyOrder(3)]
        public string? ComponentType { get; init; }

        [JsonPropertyOrder(4)]
        public string? ParentLogicalId { get; init; }

        [JsonPropertyOrder(5)]
        public string Name { get; init; } = "";

        [JsonPropertyOrder(6)]
        public int SiblingIndex { get; init; } = 0;

        [JsonPropertyOrder(7)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PrefabInstanceKey? PrefabKey { get; init; }

        [JsonPropertyOrder(8)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SourcePrefabGuid { get; init; }
    }
}
