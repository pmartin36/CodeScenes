using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public record OverrideTarget
    {
        [JsonPropertyOrder(0)]
        public string PrefabId { get; init; } = "";

        [JsonPropertyOrder(1)]
        public long ObjectId { get; init; }
    }
}
