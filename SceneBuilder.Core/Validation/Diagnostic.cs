using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Validation
{
    public sealed record Diagnostic
    {
        [JsonIgnore]
        public string File { get; init; } = "";

        [JsonPropertyName("line")]
        public int Line { get; init; }

        [JsonPropertyName("col")]
        public int Col { get; init; }

        [JsonPropertyName("code")]
        public string Code { get; init; } = "";

        [JsonPropertyName("severity")]
        public DiagnosticSeverity Severity { get; init; }

        [JsonPropertyName("message")]
        public string Message { get; init; } = "";

        [JsonPropertyName("suggestion")]
        public string? Suggestion { get; init; }
    }
}
