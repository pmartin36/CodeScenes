using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Identity
{
    /// <summary>
    /// M10 (b6-t2): one persisted (PrefabId, ObjectId, PropertyPath) override target + its recorded
    /// BaseValue, attached to a <c>Kind="PrefabInstance"</c> <see cref="IdentityMapEntry"/> so the
    /// target still resolves to the same pair key across reload, and so a future rebuild can compare
    /// the recorded BaseValue against the prefab's CURRENT default for stale detection (b3-t3).
    /// </summary>
    public record InstanceOverrideRecord
    {
        [JsonPropertyOrder(0)]
        public string PrefabId { get; init; } = "";

        [JsonPropertyOrder(1)]
        public long ObjectId { get; init; }

        [JsonPropertyOrder(2)]
        public string PropertyPath { get; init; } = "";

        [JsonPropertyOrder(3)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BaseValue { get; init; }
    }
}
