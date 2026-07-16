using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Validation
{
    // b4-t4: turns a HeadlessValidationResult into the two author-facing output shapes (text /
    // JSON) plus the process exit code. Program.cs (SceneBuilder.Validate) is a thin wrapper over
    // this — the envelope/format shapes are the LLM/tool contract and are unit-tested here in Core.
    public static class DiagnosticRenderer
    {
        public const string PlanningBoundaryNote =
            "(planning-phase; run a Unity Build to validate execution-phase.)";

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string RenderText(HeadlessValidationResult result)
        {
            var sb = new StringBuilder();

            foreach (var d in result.Result.Diagnostics)
            {
                var severity = d.Severity.ToString().ToLowerInvariant();
                sb.Append(d.File).Append(':').Append(d.Line).Append(':').Append(d.Col)
                  .Append("  ").Append(severity).Append("  ").Append(d.Code).Append("  ")
                  .Append(d.Message).AppendLine();

                if (!string.IsNullOrEmpty(d.Suggestion))
                {
                    sb.Append("    suggestion: ").Append(d.Suggestion).AppendLine();
                }
            }

            if (result.Skipped.Count > 0)
            {
                sb.Append("skipped: ").Append(string.Join(", ", result.Skipped)).AppendLine();
            }

            var errorCount = result.Result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            sb.Append(errorCount).Append(" error(s).  ").Append(PlanningBoundaryNote).AppendLine();

            return sb.ToString();
        }

        public static string RenderJson(HeadlessValidationResult result)
        {
            var dto = new JsonEnvelope
            {
                File = result.File,
                Ok = result.Ok,
                Diagnostics = result.Result.Diagnostics,
                Skipped = result.Skipped.Count == 0 ? null : result.Skipped,
                Phase = "planning",
            };

            return JsonSerializer.Serialize(dto, JsonOptions);
        }

        public static int ExitCode(HeadlessValidationResult result)
        {
            return result.Ok ? 0 : 1;
        }

        // Flat DTO for the JSON envelope — deliberately NOT HeadlessValidationResult, whose shape
        // is nested ({ File, Result:{diagnostics,ok}, Skipped, Ok }). Declaration order matches the
        // documented envelope: file, ok, diagnostics, skipped?, phase.
        private sealed class JsonEnvelope
        {
            [JsonPropertyName("file")]
            public string File { get; init; } = string.Empty;

            [JsonPropertyName("ok")]
            public bool Ok { get; init; }

            [JsonPropertyName("diagnostics")]
            public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = System.Array.Empty<Diagnostic>();

            [JsonPropertyName("skipped")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public IReadOnlyList<string>? Skipped { get; init; }

            [JsonPropertyName("phase")]
            public string Phase { get; init; } = "planning";
        }
    }
}
