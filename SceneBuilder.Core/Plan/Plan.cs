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

        [JsonPropertyOrder(3)]
        public SkippedField[] Skipped { get; init; } = Array.Empty<SkippedField>();
    }

    // A field whose ValueNode is Unsupported — no SetField is emitted; surfaced here for preview.
    public sealed record SkippedField
    {
        [JsonPropertyOrder(0)]
        public string LogicalId { get; init; } = "";

        [JsonPropertyOrder(1)]
        public string Path { get; init; } = "";

        [JsonPropertyOrder(2)]
        public string Reason { get; init; } = "Unsupported";
    }
}
