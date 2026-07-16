using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Validation
{
    public sealed record ValidationResult
    {
        [JsonPropertyName("diagnostics")]
        public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = new List<Diagnostic>();

        [JsonPropertyName("ok")]
        public bool Ok => Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
    }
}
