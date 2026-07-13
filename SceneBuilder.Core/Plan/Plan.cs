using System;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Plan
{
    public record Plan
    {
        [JsonPropertyOrder(0)]
        public int SchemaVersion { get; init; }

        [JsonPropertyOrder(1)]
        public string ScenePath { get; init; } = "";

        [JsonPropertyOrder(2)]
        public PlanOp[] Ops { get; init; } = Array.Empty<PlanOp>();
    }
}
