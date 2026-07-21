using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public record PropertyOverride
    {
        [JsonPropertyOrder(0)]
        public OverrideTarget Target { get; init; } = new();

        [JsonPropertyOrder(1)]
        public string PropertyPath { get; init; } = "";

        [JsonPropertyOrder(2)]
        public ValueNode Value { get; init; } = new ValueNode.Unsupported("");

        [JsonPropertyOrder(3)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ValueNode? ObjectReference { get; init; }

        [JsonPropertyOrder(4)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ValueNode? BaseValue { get; init; }
    }
}
