using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Plan
{
    public record CreateObject : PlanOp
    {
        [JsonPropertyOrder(0)]
        public string LogicalId { get; init; } = "";

        [JsonPropertyOrder(1)]
        public string Name { get; init; } = "";
    }
}
