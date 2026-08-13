using System;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public record SceneModel
    {
        [JsonPropertyOrder(0)]
        public int SchemaVersion { get; init; }

        [JsonPropertyOrder(1)]
        public GameObjectNode[] Roots { get; init; } = Array.Empty<GameObjectNode>();

        // Non-null marks this model as a prefab VARIANT and carries its base prefab's ref;
        // omitted from serialization when null so an unrelated (non-variant) model's JSON is
        // byte-identical to before this field existed.
        [JsonPropertyOrder(2)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AssetRef? VariantBase { get; init; }
    }
}
