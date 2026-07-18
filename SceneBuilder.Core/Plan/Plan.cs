using System;
using System.Text.Json.Serialization;
using SceneBuilder.Core.Validation;

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

        // b3-t2: informational diagnostics carried out of the Differ (e.g. prefab overrides
        // preserved but not modelled). NOT persisted — must stay JsonIgnore so canonical Plan
        // JSON is byte-identical, and NOT counted by DriftState (Ops/Skipped only). Population is
        // the code-writer's job; this is the data carrier stub only.
        [JsonIgnore]
        public System.Collections.Generic.IReadOnlyList<Diagnostic> Diagnostics { get; init; }
            = Array.Empty<Diagnostic>();
    }

    // A field the plan does NOT write, surfaced for preview: Reason "Unsupported" (the ValueNode
    // could not be parsed) or "Unresolved" (an asset ref named a target that did not resolve —
    // skipped rather than written as a null-GUID clear, which would destroy the live value).
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
