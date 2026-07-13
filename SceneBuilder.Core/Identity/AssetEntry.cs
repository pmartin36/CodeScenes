using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Identity
{
    public record AssetEntry
    {
        [JsonPropertyOrder(0)]
        public string Guid { get; init; } = "";

        [JsonPropertyOrder(1)]
        public string LastKnownPath { get; init; } = "";

        [JsonPropertyOrder(2)]
        public string TypeHint { get; init; } = "";
    }
}
