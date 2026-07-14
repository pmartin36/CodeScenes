using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public record ComponentData
    {
        [JsonPropertyOrder(0)]
        public string LogicalId { get; init; } = "";

        [JsonPropertyOrder(1)]
        public TypeRef Type { get; init; } = new TypeRef("");

        [JsonPropertyOrder(2)]
        public FieldMap Fields { get; init; } = FieldMap.Empty;
    }
}
