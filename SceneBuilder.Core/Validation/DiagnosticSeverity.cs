using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Validation
{
    [JsonConverter(typeof(DiagnosticSeverityJsonConverter))]
    public enum DiagnosticSeverity
    {
        Error,
        Info
    }

    public sealed class DiagnosticSeverityJsonConverter : JsonConverter<DiagnosticSeverity>
    {
        public override DiagnosticSeverity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return Enum.Parse<DiagnosticSeverity>(reader.GetString()!, ignoreCase: true);
        }

        public override void Write(Utf8JsonWriter writer, DiagnosticSeverity value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString().ToLowerInvariant());
        }
    }
}
