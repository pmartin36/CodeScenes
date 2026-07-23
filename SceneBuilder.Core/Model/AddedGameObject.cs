using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    /// <summary>
    /// A child GameObject added to a prefab instance. <see cref="Parent"/>.SubKey identifies the
    /// in-instance parent the child was dragged/authored under ("" / default == the instance root).
    /// </summary>
    public record AddedGameObject
    {
        [JsonPropertyOrder(0)]
        public OverrideTarget Parent { get; init; } = new();

        [JsonPropertyOrder(1)]
        public GameObjectNode Node { get; init; } = new();
    }
}
