using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public record PrefabInstanceKey
    {
        [JsonPropertyOrder(0)]
        public ulong TargetPrefabId { get; init; }

        [JsonPropertyOrder(1)]
        public ulong TargetObjectId { get; init; }
    }
}
