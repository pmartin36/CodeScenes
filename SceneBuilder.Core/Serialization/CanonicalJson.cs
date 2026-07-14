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
            };

            options.Converters.Add(new FieldMapJsonConverter());

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
