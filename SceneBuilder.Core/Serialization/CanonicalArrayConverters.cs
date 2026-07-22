using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using SceneBuilder.Core.Model;

namespace SceneBuilder.Core.Serialization
{
    // Base for element-array converters that make override-collection arrays byte-stable
    // regardless of input order (canonical determinism, see CanonicalJson.BuildOptions).
    // Sorts a COPY at Write only (model is never mutated); Read passes elements through in
    // file order (already canonical on disk). Mirrors FieldMapJsonConverter's sort-on-Write
    // pattern for object keys, applied here to array elements.
    internal abstract class CanonicalArrayConverter<T> : JsonConverter<T[]>
    {
        protected abstract int Compare(T a, T b);

        public override T[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException($"Expected StartArray for {typeof(T).Name}[].");
            }

            var items = new List<T>();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return items.ToArray();
                }

                var item = JsonSerializer.Deserialize<T>(ref reader, options)!;
                items.Add(item);
            }

            throw new JsonException($"Unexpected end of JSON while reading {typeof(T).Name}[].");
        }

        public override void Write(Utf8JsonWriter writer, T[] value, JsonSerializerOptions options)
        {
            var sorted = new List<T>(value);
            sorted.Sort(Compare);

            writer.WriteStartArray();

            foreach (var item in sorted)
            {
                JsonSerializer.Serialize(writer, item, options);
            }

            writer.WriteEndArray();
        }
    }

    // PropertyOverride[]: sort by (Target.PrefabId ordinal, Target.ObjectId, PropertyPath ordinal).
    internal sealed class PropertyOverrideArrayConverter : CanonicalArrayConverter<PropertyOverride>
    {
        protected override int Compare(PropertyOverride a, PropertyOverride b)
        {
            var cmp = string.CompareOrdinal(a.Target.PrefabId, b.Target.PrefabId);
            if (cmp != 0) return cmp;

            cmp = a.Target.ObjectId.CompareTo(b.Target.ObjectId);
            if (cmp != 0) return cmp;

            return string.CompareOrdinal(a.PropertyPath, b.PropertyPath);
        }
    }

    // AddedComponent[]: sort by (Target.PrefabId ordinal, Target.ObjectId, Component.Type.FullName ordinal).
    internal sealed class AddedComponentArrayConverter : CanonicalArrayConverter<AddedComponent>
    {
        protected override int Compare(AddedComponent a, AddedComponent b)
        {
            var cmp = string.CompareOrdinal(a.Target.PrefabId, b.Target.PrefabId);
            if (cmp != 0) return cmp;

            cmp = a.Target.ObjectId.CompareTo(b.Target.ObjectId);
            if (cmp != 0) return cmp;

            return string.CompareOrdinal(a.Component.Type.FullName, b.Component.Type.FullName);
        }
    }

    // OverrideTarget[]: sort by (PrefabId ordinal, ObjectId). Covers RemovedComponents AND
    // RemovedGameObjects (both are OverrideTarget[]).
    internal sealed class OverrideTargetArrayConverter : CanonicalArrayConverter<OverrideTarget>
    {
        protected override int Compare(OverrideTarget a, OverrideTarget b)
        {
            var cmp = string.CompareOrdinal(a.PrefabId, b.PrefabId);
            if (cmp != 0) return cmp;

            return a.ObjectId.CompareTo(b.ObjectId);
        }
    }
}
