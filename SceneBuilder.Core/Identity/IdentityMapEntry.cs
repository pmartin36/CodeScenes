using System.Text.Json.Serialization;

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
    }
}
