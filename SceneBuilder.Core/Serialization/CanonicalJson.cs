using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Serialization
{
    public static class CanonicalJson
    {
        private static readonly JsonSerializerOptions _options = BuildOptions(null);

        public static JsonSerializerOptions Options => _options;

        public static JsonSerializerOptions CreateOptions(IEnumerable<JsonConverter>? converters = null)
            => BuildOptions(converters);

        public static string Serialize<T>(T value, JsonSerializerOptions? options = null)
            => JsonSerializer.Serialize(value, options ?? Options).Replace("\r\n", "\n");

        public static T Deserialize<T>(string json, JsonSerializerOptions? options = null)
            => JsonSerializer.Deserialize<T>(json, options ?? Options)!;

        private static JsonSerializerOptions BuildOptions(IEnumerable<JsonConverter>? converters)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                // A live component field can legitimately hold Infinity/NaN (e.g. Unity's own
                // Joint.breakForce/breakTorque default to Mathf.Infinity) — the canonical form must be
                // able to round-trip it rather than throwing.
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            };

            options.Converters.Add(new FieldMapJsonConverter());
            options.Converters.Add(new PropertyOverrideArrayConverter());
            options.Converters.Add(new AddedComponentArrayConverter());
            options.Converters.Add(new OverrideTargetArrayConverter());
            options.Converters.Add(new AddedGameObjectArrayConverter());

            if (converters != null)
            {
                foreach (var converter in converters)
                {
                    options.Converters.Add(converter);
                }
            }

            return options;
        }
    }
}
