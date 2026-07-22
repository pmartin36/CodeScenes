using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public record AddedComponent
    {
        [JsonPropertyOrder(0)]
        public OverrideTarget Target { get; init; } = new();

        [JsonPropertyOrder(1)]
        public ComponentData Component { get; init; } = new();
    }
}
