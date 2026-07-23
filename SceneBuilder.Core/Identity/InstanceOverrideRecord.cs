using SceneBuilder.Core.Model;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Identity
{
    /// <summary>
    /// One persisted override/added/removed entry attached to a <c>Kind="PrefabInstance"</c>
    /// <see cref="IdentityMapEntry"/>, so the target still resolves to the same durable
    /// <see cref="PrefabInstanceKey"/> across reload, and so a future rebuild can compare the
    /// recorded BaseValue against the prefab's CURRENT default for stale detection.
    /// </summary>
    public record InstanceOverrideRecord
    {
        [JsonPropertyOrder(0)]
        public PrefabInstanceKey SubKey { get; init; } = new();

        [JsonPropertyOrder(1)]
        public string ComponentType { get; init; } = "";

        [JsonPropertyOrder(2)]
        public string PropertyPath { get; init; } = "";

        [JsonPropertyOrder(3)]
        public string Kind { get; init; } = "";

        [JsonPropertyOrder(4)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BaseValue { get; init; }
    }
}
