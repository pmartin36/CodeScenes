using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Serialization
{
    // Serializes FieldMap as a JSON object with keys in StringComparer.Ordinal-sorted
    // order (canonical determinism, see CanonicalJson.BuildOptions), regardless of the
    // map's insertion order. Keys are written verbatim (not camelCased) since they are
    // data (e.g. "m_Mass", "member:mass", "_health"), not member names.
    public sealed class FieldMapJsonConverter : JsonConverter<FieldMap>
    {
        public override FieldMap Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Expected StartObject for FieldMap.");
            }

            var entries = new List<KeyValuePair<string, ValueNode>>();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return new FieldMap(entries);
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("Expected PropertyName in FieldMap object.");
                }

                var key = reader.GetString()!;
                reader.Read();
                var value = JsonSerializer.Deserialize<ValueNode>(ref reader, options)!;
                entries.Add(new KeyValuePair<string, ValueNode>(key, value));
            }

            throw new JsonException("Unexpected end of JSON while reading FieldMap.");
        }

        public override void Write(Utf8JsonWriter writer, FieldMap value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            var sorted = new List<KeyValuePair<string, ValueNode>>(value);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            foreach (var kv in sorted)
            {
                writer.WritePropertyName(kv.Key);
                JsonSerializer.Serialize(writer, kv.Value, options);
            }

            writer.WriteEndObject();
        }
    }
}
